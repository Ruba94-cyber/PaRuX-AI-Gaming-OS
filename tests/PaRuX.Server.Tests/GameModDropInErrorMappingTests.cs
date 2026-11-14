using System;
using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PaRuX.Server.Orchestration.Gaming;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>
/// Real-app finding (2026-11-14): a corrupt, tampered or traversal ZIP was rejected correctly but answered
/// HTTP 500 "An interrupted receipt may require cleanup", and a ZIP held open by another program on removal
/// answered 500. A refused package is a client error (400); an in-use file is a retryable conflict (409).
/// </summary>
public sealed class GameModDropInErrorMappingTests
{
    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    [Theory]
    [InlineData(typeof(InvalidDataException), 400)]
    [InlineData(typeof(JsonException), 400)]
    [InlineData(typeof(ArgumentException), 400)]
    [InlineData(typeof(InvalidOperationException), 409)]
    [InlineData(typeof(IOException), 500)]
    [InlineData(typeof(UnauthorizedAccessException), 500)]
    public void Install_failure_status_distinguishes_refused_package_from_io_fault(Type exceptionType, int expected)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "synthetic")!;
        Assert.Equal(expected, Status(GameAssetModEndpoints.MapInstallFailure(ex)));
    }

    [Theory]
    [InlineData(typeof(IOException), 409)]
    [InlineData(typeof(UnauthorizedAccessException), 409)]
    [InlineData(typeof(InvalidOperationException), 409)]
    [InlineData(typeof(ArgumentException), 400)]
    [InlineData(typeof(InvalidDataException), 500)]
    public void Remove_failure_status_reports_a_locked_zip_as_a_conflict(Type exceptionType, int expected)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "synthetic")!;
        Assert.Equal(expected, Status(GameAssetModEndpoints.MapRemoveFailure(ex)));
    }

    [Fact]
    public void A_refused_package_never_blames_an_interrupted_receipt()
    {
        var result = GameAssetModEndpoints.MapInstallFailure(new InvalidDataException("Expected replacement PNG not found."));
        string json = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        Assert.Contains("Expected replacement PNG not found.", json);
        Assert.DoesNotContain("interrupted", json, StringComparison.OrdinalIgnoreCase);
    }
}
