using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Nexode.App.Services;

/// <summary>
/// Clipboard with protection for secrets: Windows clipboard history and cloud sync are excluded, other platforms get
/// the "concealed" hints, and the value is cleared after N seconds if it is still on the clipboard.
/// </summary>
public sealed class SecureClipboard
{
    private DispatcherTimer? _timer;
    private string? _secret;
    private IClipboard? _clipboard;
    private int _remaining;

    /// <summary>Seconds until the secret is cleared (0 = nothing pending).</summary>
    public event Action<int>? CountdownChanged;

    public async Task CopyAsync(TopLevel? top, string text, bool sensitive)
    {
        var clipboard = top?.Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(text)) return;

        if (sensitive)
        {
            try
            {
                var item = new DataTransferItem();
                item.SetText(text);
                if (OperatingSystem.IsWindows())
                {
                    item.Set(DataFormat.CreateBytesPlatformFormat("ExcludeClipboardContentFromMonitorProcessing"), new byte[] { 0, 0, 0, 0 });
                    item.Set(DataFormat.CreateBytesPlatformFormat("CanIncludeInClipboardHistory"), new byte[] { 0, 0, 0, 0 });
                    item.Set(DataFormat.CreateBytesPlatformFormat("CanUploadToCloudClipboard"), new byte[] { 0, 0, 0, 0 });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    item.Set(DataFormat.CreateStringPlatformFormat("org.nspasteboard.ConcealedType"), text);
                }
                else
                {
                    item.Set(DataFormat.CreateBytesPlatformFormat("x-kde-passwordManagerHint"), "secret"u8.ToArray());
                }

                var data = new DataTransfer();
                data.Add(item);
                await clipboard.SetDataAsync(data);
            }
            catch (Exception)
            {
                await clipboard.SetTextAsync(text);
            }

            StartCountdown(clipboard, text);
        }
        else
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void StartCountdown(IClipboard clipboard, string secret)
    {
        var seconds = AppHost.Settings.ClipboardClearSeconds;
        if (seconds <= 0) return;
        _clipboard = clipboard;
        _secret = secret;
        _remaining = seconds;
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) =>
        {
            _remaining--;
            CountdownChanged?.Invoke(Math.Max(0, _remaining));
            if (_remaining > 0) return;
            _timer?.Stop();
            await ClearIfOursAsync();
        };
        _timer.Start();
        CountdownChanged?.Invoke(_remaining);
    }

    public async Task ClearIfOursAsync()
    {
        if (_clipboard is null || _secret is null) return;
        try
        {
            var current = await _clipboard.TryGetTextAsync();
            if (current == _secret) await _clipboard.ClearAsync();
        }
        catch (Exception)
        {
            // Clipboard busy: nothing else to do.
        }

        _secret = null;
        CountdownChanged?.Invoke(0);
    }
}
