using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace XiaobianPet.Services;

internal sealed class Mp3Player
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlaybackPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumPlaybackTimeout = TimeSpan.FromMinutes(2);

    public void Play(byte[] mp3Audio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mp3Audio);
        if (mp3Audio.Length == 0)
        {
            throw new ArgumentException("MP3 音频不能为空。", nameof(mp3Audio));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("MP3 播放仅支持 Windows。");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"xiaobian-tts-{Guid.NewGuid():N}.mp3");
        MediaPlayer? player = null;

        try
        {
            File.WriteAllBytes(temporaryPath, mp3Audio);

            // MediaPlayer is created and pumped on SpeechService's dedicated STA thread.
            // It therefore never waits on the UI Dispatcher while MainWindow is disposing.
            var dispatcher = Dispatcher.CurrentDispatcher;
            player = new MediaPlayer();
            var isOpened = false;
            var hasEnded = false;
            Exception? mediaFailure = null;

            player.MediaOpened += (_, _) => isOpened = true;
            player.MediaEnded += (_, _) => hasEnded = true;
            player.MediaFailed += (_, args) => mediaFailure = args.ErrorException;
            player.Open(new System.Uri(temporaryPath, System.UriKind.Absolute));

            var openTimer = Stopwatch.StartNew();
            while (!isOpened && mediaFailure is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (openTimer.Elapsed >= OpenTimeout)
                {
                    throw new TimeoutException("等待 MP3 音频打开超时。");
                }

                PumpDispatcherOnce(dispatcher);
            }

            if (mediaFailure is not null)
            {
                throw new InvalidOperationException("无法打开 MP3 音频。", mediaFailure);
            }

            player.Play();
            var expectedPlaybackTimeout = player.NaturalDuration.HasTimeSpan
                ? player.NaturalDuration.TimeSpan + TimeSpan.FromSeconds(30)
                : MaximumPlaybackTimeout;
            var playbackTimeout = expectedPlaybackTimeout <= MaximumPlaybackTimeout
                ? expectedPlaybackTimeout
                : MaximumPlaybackTimeout;
            var playbackTimer = Stopwatch.StartNew();
            while (!hasEnded && mediaFailure is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (playbackTimer.Elapsed >= playbackTimeout)
                {
                    throw new TimeoutException("等待 MP3 音频播放完成超时。");
                }

                PumpDispatcherOnce(dispatcher);
            }

            if (mediaFailure is not null)
            {
                throw new InvalidOperationException("MP3 音频播放失败。", mediaFailure);
            }
        }
        finally
        {
            if (player is not null)
            {
                try
                {
                    player.Stop();
                }
                catch
                {
                }

                try
                {
                    player.Close();
                }
                catch
                {
                }
            }

            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void PumpDispatcherOnce(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            PlaybackPollInterval,
            DispatcherPriority.Background,
            (_, _) => frame.Continue = false,
            dispatcher);

        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(20);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
