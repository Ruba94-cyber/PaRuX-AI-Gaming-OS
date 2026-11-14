using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

/// <summary>The Diffusers engine reason code is shown as a truthful, localized message (one language per screen).</summary>
public sealed class DiffusersEngineStatusPresenterTests
{
    private static string En(string en, string it) => en;
    private static string It(string en, string it) => it;

    [Theory]
    [InlineData("runtime_missing", "not installed yet", "non è ancora installato")]
    [InlineData("model_missing", "pinned model snapshot", "istantanea del modello")]
    [InlineData("cuda_unavailable", "cannot use a CUDA GPU", "non può usare una GPU CUDA")]
    [InlineData("install_failed", "incomplete or failed", "incompleta o non riuscita")]
    public void Each_code_has_its_own_english_and_italian_text(string code, string english, string italian)
    {
        var status = new ManagedMediaEngineStatus { StatusCode = code, Detail = "server detail in English" };
        string en = DiffusersEngineStatusPresenter.Describe(status, En);
        string it = DiffusersEngineStatusPresenter.Describe(status, It);
        Assert.Contains(english, en);
        Assert.Contains(italian, it);
        Assert.DoesNotContain("server detail", en);
        Assert.DoesNotContain("server detail", it); // known codes never mix the English server text into an Italian screen
    }

    [Fact]
    public void The_four_not_ready_codes_are_distinct()
    {
        var texts = new[] { "runtime_missing", "model_missing", "cuda_unavailable", "install_failed" }
            .Select(c => DiffusersEngineStatusPresenter.Describe(new ManagedMediaEngineStatus { StatusCode = c }, En)).ToArray();
        Assert.Equal(4, texts.Distinct().Count());
    }

    [Fact]
    public void Ready_and_unknown_codes_are_honest()
    {
        Assert.Contains("ready", DiffusersEngineStatusPresenter.Describe(new ManagedMediaEngineStatus { Ready = true, StatusCode = "ready" }, En));
        Assert.Equal("something new", DiffusersEngineStatusPresenter.Describe(new ManagedMediaEngineStatus { StatusCode = "x", Detail = "something new" }, En));
        Assert.Contains("not ready", DiffusersEngineStatusPresenter.Describe(new ManagedMediaEngineStatus(), En));
    }

    [Theory]
    [InlineData("runtime_missing", true)]
    [InlineData("model_missing", true)]
    [InlineData("install_failed", true)]
    [InlineData("cuda_unavailable", false)]
    [InlineData("ready", false)]
    public void Install_is_offered_only_when_it_can_help(string code, bool offered) =>
        Assert.Equal(offered, DiffusersEngineStatusPresenter.CanInstall(new ManagedMediaEngineStatus { StatusCode = code, Ready = code == "ready" }));
}
