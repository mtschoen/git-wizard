namespace GitWizard.Watch.Linux;

// The helper-output half: replays each frame, and turns the end of the helper into one death.
public sealed partial class ElevatedHelperVolumeChangeSource
{
    async Task ReadMessagesAsync(LinuxWatchHelperProcess process)
    {
        string? diedReason = null;
        try
        {
            using var reader = new StreamReader(process.Output, Utf8WithoutMarker, false, 4096);
            while (diedReason is null && await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!LinuxWatchHelperProtocol.TryParse(line, out var frame, out var error))
                {
                    GitWizardLog.Log($"Ignored a malformed line from the elevated Live helper: {error}", GitWizardLog.LogType.Warning);
                    continue;
                }

                diedReason = Replay(frame);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // An unreadable stdout ends the helper exactly as its end-of-output does.
        }

        await EndAsync(process, diedReason).ConfigureAwait(false);
    }

    // Returns the reason when the frame ends the helper.
    string? Replay(LinuxWatchHelperFrame frame)
    {
        switch (frame)
        {
            case HelloFrame hello when hello.ProtocolVersion != LinuxWatchHelperProtocol.Version:
                return $"The elevated Live helper speaks protocol {hello.ProtocolVersion}, not {LinuxWatchHelperProtocol.Version}.";
            case HelloFrame:
                lock (_launchGate)
                {
                    _helloReceived = true;
                }

                _hello.TrySetResult();
                Connected?.Invoke();
                break;
            case ArmResultFrame armResult:
                _armResult.TrySetResult(armResult.Errors);
                break;
            case ReadinessFrame readiness:
                PublishReadiness(_ => MergeOverLocalExclusions(readiness.Volumes));
                break;
            case PumpStartedFrame:
                _pumpStarted.TrySetResult();
                break;
            case BatchFrame batch:
                _batches.Writer.TryWrite(batch.Batch);
                break;
            case DriveFailedFrame driveFailed:
                DriveFailed?.Invoke(driveFailed.Volume, driveFailed.Message);
                break;
            case ErrorFrame error:
                GitWizardLog.Log($"The elevated Live helper rejected a request: {error.Message}", GitWizardLog.LogType.Error);
                // Only the arm can be waiting on an answer; a rejected arm must not hang it.
                _armResult.TrySetResult(_roots.ToDictionary(root => root, _ => RejectedRequestMessage, WatchPath.StringComparer));
                break;
            case DiedFrame died:
                return died.Reason;
            default:
                GitWizardLog.Log($"Ignored an unexpected {frame.GetType().Name} from the elevated Live helper.", GitWizardLog.LogType.Warning);
                break;
        }

        return null;
    }

    async Task EndAsync(LinuxWatchHelperProcess process, string? diedReason)
    {
        var stoppedHere = _disposing || _terminated;
        var reason = stoppedHere ? StoppedMessage
            : diedReason ?? DescribeEnd(await TryGetExitCodeAsync(process).ConfigureAwait(false));
        // The death is recorded before the arm can observe the end, so an arm that fails on it
        // returns to a source whose readiness and death already describe that failure. It is also
        // published before the batch stream ends: the watch service detaches its death handler when
        // enumeration ends, so completing first could lose the reason. An end this source caused
        // (disposal, or terminating pkexec on cancellation) is not a death.
        if (!stoppedHere)
        {
            ReportDeath(reason);
        }

        _batches.Writer.TryComplete();
        _ended.TrySetResult(reason);
    }

    void ReportDeath(string reason)
    {
        // A fallback for a helper that died without reporting its own terminal readiness: only roots
        // that are missing or not already Excluded take the death reason, so a root excluded earlier
        // (at arming, or by the helper's terminal frame) keeps its own reason. One snapshot, and none
        // when nothing changed.
        PublishReadiness(snapshot =>
        {
            var alreadyExcluded = snapshot.ExcludedVolumes.Select(status => status.Volume).ToHashSet(WatchKeyComparer.Instance);
            var excluded = _roots
                .Where(root => !alreadyExcluded.Contains(root))
                .Select(root => VolumeWatchStatus.Excluded(root, VolumeBlockOrigin.Unknown, reason))
                .ToArray();
            return excluded.Length == 0 ? snapshot : snapshot.WithAll(excluded);
        });

        Action<string>? handlers;
        lock (_gate)
        {
            if (_deathReason is not null)
            {
                return;
            }

            _deathReason = reason;
            handlers = _sourceDied;
        }

        handlers?.Invoke(reason);
    }

    async Task<int?> TryGetExitCodeAsync(LinuxWatchHelperProcess process)
    {
        try
        {
            return await process.Exited.WaitAsync(_exitWait, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    // Some desktop agents report a dismissed dialog as 127 rather than 126.
    string DescribeEnd(int? exitCode)
    {
        var suffix = exitCode is { } code ? $" (exit code {code})" : string.Empty;
        if (_helloReceived)
        {
            return $"The elevated Live helper ended unexpectedly{suffix}.";
        }

        return exitCode switch
        {
            PkexecDismissedExitCode => AuthenticationCancelledMessage,
            PkexecNotAuthorizedExitCode => AuthorizationFailedMessage,
            _ => $"The elevated Live helper ended before it started{suffix}."
        };
    }

    static async Task DrainErrorAsync(Stream error)
    {
        try
        {
            using var reader = new StreamReader(error, Utf8WithoutMarker, false, 4096);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    GitWizardLog.Log($"Elevated Live helper: {line}", GitWizardLog.LogType.Warning);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Diagnostics only; a closed stderr loses nothing the user sees.
        }
    }
}
