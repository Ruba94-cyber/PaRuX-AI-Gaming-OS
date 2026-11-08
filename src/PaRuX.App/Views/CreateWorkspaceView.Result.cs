using System;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PaRuX.Services;

namespace PaRuX.Views
{
    /// <summary>The result strip of Create: a large inline preview of the last image plus the next things a person does with it.</summary>
    public sealed partial class CreateWorkspaceView
    {
        private readonly Image _resultImage = new Image { MaxHeight = 520, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        private readonly StackPanel _resultActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly MediaPlayerElement _resultAudio = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true, Height = 110, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 720, Visibility = Visibility.Collapsed
        };
        private readonly MediaPlayerElement _resultVideo = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            AutoPlay = false,
            Height = 320,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };
        private readonly Button _resultVideoPreviewButton = new Button
        {
            Content = "Preview video", Visibility = Visibility.Collapsed
        };
        private readonly System.Collections.Generic.List<Button> _imageOnlyResultButtons = new();
        private string? _resultPath;

        private static bool IsAudioFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".flac" or ".mp3" or ".wav" or ".ogg" or ".opus" or ".m4a";
        }

        private static bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".mp4" or ".webm" or ".mov" or ".mkv";
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
        }

        private UIElement ThreedWithSetup()
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(NewSetupCard("3d", "image to 3D"));
            panel.Children.Add(BuildThreedPanel());
            return panel;
        }

        private UIElement BuildResultStrip()
        {
            var panel = new StackPanel { Spacing = 8 };
            AutomationProperties.SetAutomationId(_resultImage, "Create_Result_Image");
            AutomationProperties.SetName(_resultImage, "Generated result");
            panel.Children.Add(_resultImage);
            AutomationProperties.SetAutomationId(_resultAudio, "Create_Result_Audio");
            AutomationProperties.SetName(_resultAudio, "Generated audio player");
            panel.Children.Add(_resultAudio);
            AutomationProperties.SetAutomationId(_resultVideo, "Create_Result_VideoPreview");
            AutomationProperties.SetName(_resultVideo, "Generated local video preview");
            panel.Children.Add(_resultVideo);
            AutomationProperties.SetAutomationId(_resultVideoPreviewButton, "Create_Result_PreviewVideo_Button");
            AutomationProperties.SetName(_resultVideoPreviewButton, "Preview generated video locally");
            _resultVideoPreviewButton.Click += (_, _) => OpenInlineVideoPreview();
            panel.Children.Add(_resultVideoPreviewButton);
            _resultActions.Children.Add(ResultButton("Open", "Create_Result_Open_Button", (_, _) => LaunchResult(false)));
            _resultActions.Children.Add(ResultButton("Show in folder", "Create_Result_Folder_Button", (_, _) => LaunchResult(true)));
            _imageOnlyResultButtons.Add(ResultButton("Edit this image", "Create_Result_Edit_Button", (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                _editSourcePath = _resultPath;
                _editSourceLabel.Text = _resultPath;
                _status.Text = "Result set as the source image in IMAGE EDIT below.";
            }));
            _imageOnlyResultButtons.Add(ResultButton("Remove background", "Create_Result_RemoveBackground_Button", async (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                await RemoveBackgroundAsync(_resultPath);
            }));
            _imageOnlyResultButtons.Add(ResultButton("Animate this image", "Create_Result_Animate_Button", (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                _videoStartImagePath = _resultPath;
                _videoStartLabel.Text = "Start image: " + Path.GetFileName(_resultPath);
                _status.Text = "Result set as the first frame in VIDEO below.";
            }));
            _imageOnlyResultButtons.Add(ResultButton("Make 3D", "Create_Result_3D_Button", (_, _) =>
            {
                if (_resultPath == null || !IsImageFile(_resultPath)) return;
                _threedSourcePath = _resultPath;
                _threedSourceLabel.Text = _resultPath;
                SelectMode("3D");
                _status.Text = "Result set as the source image in IMAGE → 3D.";
            }));
            foreach (Button b in _imageOnlyResultButtons) _resultActions.Children.Add(b);
            panel.Children.Add(_resultActions);
            return panel;
        }

        private static Button ResultButton(string label, string automationId, RoutedEventHandler click)
        {
            var button = new Button { Content = label };
            AutomationProperties.SetAutomationId(button, automationId);
            button.Click += click;
            return button;
        }

        private void ShowResult(string path)
        {
            ReleaseInlineVideoPreview();
            _resultPath = path;
            _resultActions.Visibility = Visibility.Visible;
            _resultVideoPreviewButton.Visibility = IsVideoFile(path)
                ? Visibility.Visible : Visibility.Collapsed;
            bool audio = IsAudioFile(path) && File.Exists(path);
            _resultAudio.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            if (audio)
            {
                try { _resultAudio.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path)); }
                catch (Exception ex) when (ex is UriFormatException or IOException) { _resultAudio.Visibility = Visibility.Collapsed; }
            }
            else _resultAudio.Source = null;
            foreach (Button b in _imageOnlyResultButtons) b.Visibility = IsImageFile(path) ? Visibility.Visible : Visibility.Collapsed;
            bool image = IsImageFile(path) && File.Exists(path);
            _resultImage.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
            if (!image) { _resultImage.Source = null; return; }
            try
            {
                // The Create result strip is a preview, not an export. Do not
                // decode the full 4K/8K image into a permanently resident WinUI
                // bitmap on low-memory PCs; Open still accesses the original.
                _resultImage.Source = new BitmapImage(new Uri(path))
                {
                    DecodePixelWidth = 1280
                };
            }
            catch (Exception ex) when (ex is UriFormatException or IOException or UnauthorizedAccessException)
            {
                _resultImage.Source = null;
                _resultImage.Visibility = Visibility.Collapsed;
                _status.Text = "Generated image preview is unavailable; the original output was not changed.";
            }
        }

        private void OpenInlineVideoPreview()
        {
            string? path = _resultPath;
            if (string.IsNullOrWhiteSpace(path) || !IsVideoFile(path))
                return;
            ReleaseInlineVideoPreview();
            if (GamingManager.Current.IsActive || LocalAiController.Current.IsGameActive())
            {
                _status.Text = "Video preview is unavailable during Gaming. Leave the game first.";
                return;
            }

            // Reuse Gallery's exact local file/link/empty-file admission rather
            // than trusting a filename or triggering a new video job.
            var candidate = new MediaGalleryItem
            {
                Id = "create-latest-video",
                Title = Path.GetFileName(path),
                ArtifactType = "video",
                MediaType = "video/mp4",
                FilePath = path,
                Prompt = string.Empty,
                Model = "Local Media Engine",
                Status = "final",
                CreatedUtc = DateTime.UtcNow
            };
            if (MediaGalleryPreviewPolicy.KindFor(candidate) != MediaGalleryPreviewKind.Video)
            {
                _status.Text = "This video output is missing, empty or not a supported local file. "
                    + "Check the generation job or open the Gallery to recheck it.";
                return;
            }

            try
            {
                _resultVideo.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
                _resultVideo.Visibility = Visibility.Visible;
                _status.Text = "Local video preview is ready. Press Play to start; the original output is unchanged.";
            }
            catch (Exception ex)
            {
                ReleaseInlineVideoPreview();
                _status.Text = "Video preview is unavailable: " + ex.GetType().Name
                    + ". The original output is unchanged.";
            }
        }

        private void ReleaseInlineVideoPreview()
        {
            try { _resultVideo.MediaPlayer?.Pause(); }
            catch (Exception ex) { Log.Write("Create video pause: " + ex.GetType().Name); }
            try { _resultVideo.Source = null; }
            catch (Exception ex) { Log.Write("Create video release: " + ex.GetType().Name); }
            _resultVideo.Visibility = Visibility.Collapsed;
        }

        private void OnCreateGamingStateChanged(object? sender, EventArgs e)
        {
            if (!GamingManager.Current.IsActive && !LocalAiController.Current.IsGameActive())
                return;
            ReleaseInlineVideoPreview();
            if (_resultPath != null && IsVideoFile(_resultPath))
                _status.Text = "Gaming started: the video preview was stopped to leave the GPU free.";
        }

        private void LaunchResult(bool inFolder)
        {
            if (_resultPath == null || !File.Exists(_resultPath)) { _status.Text = "The result file is no longer at " + _resultPath; return; }
            try
            {
                var start = inFolder
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + _resultPath + "\"")
                    : new ProcessStartInfo(_resultPath) { UseShellExecute = true };
                Process.Start(start);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _status.Text = "Could not open the result: " + ex.Message;
            }
        }
    }
}
