using Aperture.App.Services;
using Aperture.Core.Media;
using Aperture.Core.Playback;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Aperture.App.Playback;

/// <summary>
/// Interim playback engine on Windows Media Foundation (<see cref="MediaPlayer"/>), used
/// while the libmpv engine is built. It plays what Windows itself can decode: H.264,
/// HEVC (with the Store extension), VP9 and AV1 (with extensions), AAC, MP3 and more in
/// MP4, MKV, MOV, WebM and AVI. Everything above this class talks to <see cref="IPlaybackEngine"/>.
/// </summary>
internal sealed class MediaFoundationEngine : IPlaybackEngine
{
    private readonly DispatcherQueue _dispatcher;
    private MediaSource? _source;
    private TimeSpan _startAt;
    private bool _disposed;

    public MediaFoundationEngine(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        Player = new MediaPlayer
        {
            AutoPlay = false,
            AudioCategory = MediaPlayerAudioCategory.Movie,
        };
        Player.MediaOpened += OnMediaOpened;
        Player.MediaFailed += OnMediaFailed;
        Player.MediaEnded += OnMediaEnded;
        Player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    /// <summary>The player, for the video surface (<c>MediaPlayerElement.SetMediaPlayer</c>).</summary>
    public MediaPlayer Player { get; }

    public PlaybackStatus Status { get; private set; } = PlaybackStatus.Idle;

    public PlaybackFailure? Failure { get; private set; }

    public TimeSpan Position => _source is null ? TimeSpan.Zero : Player.PlaybackSession.Position;

    public TimeSpan Duration { get; private set; }

    public double Volume
    {
        get => Player.Volume;
        set => Player.Volume = Math.Clamp(value, 0, 1);
    }

    public bool IsMuted
    {
        get => Player.IsMuted;
        set => Player.IsMuted = value;
    }

    public double Rate
    {
        get => Player.PlaybackSession.PlaybackRate;
        set => Player.PlaybackSession.PlaybackRate = Math.Clamp(value, 0.25, 4);
    }

    public event EventHandler? StatusChanged;

    public async Task OpenAsync(string path, TimeSpan startAt, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Close();

        if (!File.Exists(path))
        {
            Fail(new PlaybackFailure(PlaybackFailureKind.NotFound));
            return;
        }

        if (!MediaFileTypes.IsVideoFile(path))
        {
            Fail(new PlaybackFailure(PlaybackFailureKind.Unsupported, Path.GetExtension(path)));
            return;
        }

        SetStatus(PlaybackStatus.Opening);
        _startAt = startAt;

        StorageFile file;
        try
        {
            file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(true);
        }
        catch (FileNotFoundException)
        {
            Fail(new PlaybackFailure(PlaybackFailureKind.NotFound));
            return;
        }
        catch (UnauthorizedAccessException error)
        {
            AppLog.Warning("Access to a media file was denied", error);
            Fail(new PlaybackFailure(PlaybackFailureKind.Failed, "Windows denied access to this file."));
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _source = MediaSource.CreateFromStorageFile(file);
        Player.Source = _source;
    }

    public void Play()
    {
        if (_source is null)
        {
            return;
        }

        if (Status == PlaybackStatus.Ended)
        {
            Player.PlaybackSession.Position = TimeSpan.Zero;
        }

        Player.Play();
    }

    public void Pause()
    {
        if (_source is not null)
        {
            Player.Pause();
        }
    }

    public void Seek(TimeSpan position)
    {
        if (_source is null || !Player.PlaybackSession.CanSeek)
        {
            return;
        }

        var target = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (Duration > TimeSpan.Zero && target > Duration)
        {
            target = Duration;
        }

        Player.PlaybackSession.Position = target;
        if (Status == PlaybackStatus.Ended && target < Duration)
        {
            SetStatus(PlaybackStatus.Paused);
        }
    }

    public void Close()
    {
        if (_source is not null)
        {
            Player.Pause();
            Player.Source = null;
            _source.Dispose();
            _source = null;
        }

        Duration = TimeSpan.Zero;
        Failure = null;
        SetStatus(PlaybackStatus.Idle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Player.MediaOpened -= OnMediaOpened;
        Player.MediaFailed -= OnMediaFailed;
        Player.MediaEnded -= OnMediaEnded;
        Player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        Player.Source = null;
        _source?.Dispose();
        _source = null;
        Player.Dispose();
    }

    // MediaPlayer raises its events on worker threads; every handler hops to the UI thread
    // and ignores events that belong to a file that has since been closed.
    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        var source = _source;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !ReferenceEquals(source, _source))
            {
                return;
            }

            Duration = Player.PlaybackSession.NaturalDuration;
            if (_startAt > TimeSpan.Zero && _startAt < Duration)
            {
                Player.PlaybackSession.Position = _startAt;
            }

            Player.Play();
        });
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var source = _source;
        var failure = args.Error switch
        {
            MediaPlayerError.SourceNotSupported => new PlaybackFailure(PlaybackFailureKind.Unsupported, args.ErrorMessage),
            _ => new PlaybackFailure(PlaybackFailureKind.Failed, string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage),
        };
        AppLog.Warning($"Playback failed: {args.Error} (0x{args.ExtendedErrorCode?.HResult ?? 0:X8})");

        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && ReferenceEquals(source, _source))
            {
                Fail(failure);
            }
        });
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        var source = _source;
        _dispatcher.TryEnqueue(() =>
        {
            if (!_disposed && ReferenceEquals(source, _source))
            {
                SetStatus(PlaybackStatus.Ended);
            }
        });
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        var source = _source;
        var state = sender.PlaybackState;
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed || !ReferenceEquals(source, _source) || Status is PlaybackStatus.Failed)
            {
                return;
            }

            switch (state)
            {
                case MediaPlaybackState.Playing:
                    SetStatus(PlaybackStatus.Playing);
                    break;
                case MediaPlaybackState.Paused when Status is not PlaybackStatus.Opening and not PlaybackStatus.Ended:
                    SetStatus(PlaybackStatus.Paused);
                    break;
            }
        });
    }

    private void Fail(PlaybackFailure failure)
    {
        Failure = failure;
        SetStatus(PlaybackStatus.Failed);
    }

    private void SetStatus(PlaybackStatus status)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
