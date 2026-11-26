using System.Diagnostics;
using HidSharp;

namespace PcmonProbe;

readonly record struct KrakenStatus(
    double Liquid, int Pump, int PumpDuty, int Fan, int FanDuty,
    double LatencyMs, int Drained, int Skipped, byte[] Raw);

/// <summary>
/// The minimal Kraken reader from ADR 0004: clear the queue, send the status request, and take the
/// first status reply that arrives after it, whoever asked for it. Read-only: it writes nothing but
/// the status request, and never sends liquidctl's initialization sequence or a firmware request.
/// Byte offsets follow liquidctl's kraken3.py and LibreHardwareMonitor's KrakenV3.cs.
/// </summary>
sealed class KrakenReader : IDisposable
{
    const int VendorId = 0x1E71, ProductId = 0x3012; // Kraken Elite RGB v2 (2024)
    static readonly byte[] StatusRequest = [0x74, 0x01];

    HidStream? _stream;
    byte[] _buffer = new byte[64];
    DateTime _nextOpenAttempt = DateTime.MinValue;
    bool _announced;

    public string LastError { get; private set; } = "";

    public KrakenStatus? Read(TimeSpan budget)
    {
        if (!EnsureOpen()) return null;
        var stream = _stream!;
        try
        {
            // 1. Clear the queue: everything already waiting is a reply to an older request
            //    (usually FanControl's), so it says nothing about now.
            int drained = 0;
            stream.ReadTimeout = 1;
            while (drained < 512)
            {
                try { stream.Read(_buffer); drained++; }
                catch (TimeoutException) { break; }
            }

            // 2. Ask, then 3. take the first status reply after the request.
            var sinceRequest = Stopwatch.StartNew();
            stream.Write(StatusRequest);
            int skipped = 0;
            while (true)
            {
                int left = (int)(budget - sinceRequest.Elapsed).TotalMilliseconds;
                if (left <= 0)
                {
                    LastError = $"no status reply within {budget.TotalMilliseconds:0} ms (drained {drained}, skipped {skipped})";
                    return null;
                }
                stream.ReadTimeout = left;
                int n;
                try { n = stream.Read(_buffer); }
                catch (TimeoutException) { continue; }

                if (n < 26 || _buffer[0] != 0x75 || _buffer[1] != 0x01) { skipped++; continue; }

                var raw = _buffer[..Math.Min(n, 64)];
                if (_buffer[15] == 0xFF && _buffer[16] == 0xFF)
                {
                    LastError = $"liquid reads 0xFFFF (firmware fault?) raw={Convert.ToHexString(raw)}";
                    return null;
                }
                return new KrakenStatus(
                    Liquid: _buffer[15] + _buffer[16] / 10.0,
                    Pump: _buffer[18] << 8 | _buffer[17],
                    PumpDuty: _buffer[19],
                    Fan: _buffer[24] << 8 | _buffer[23],
                    FanDuty: _buffer[25],
                    LatencyMs: sinceRequest.Elapsed.TotalMilliseconds,
                    Drained: drained,
                    Skipped: skipped,
                    Raw: raw);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            LastError = $"HID error: {ex.Message}; will reopen";
            Close();
            return null;
        }
    }

    bool EnsureOpen()
    {
        if (_stream != null) return true;
        if (DateTime.UtcNow < _nextOpenAttempt) return false;
        _nextOpenAttempt = DateTime.UtcNow.AddSeconds(5);

        var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
        if (devices.Count == 0)
        {
            LastError = "no Kraken HID device (1E71:3012) found";
            return false;
        }
        if (!_announced)
        {
            _announced = true;
            foreach (var d in devices)
                Log.Line($"kraken: found HID '{ProductName(d)}' in={d.GetMaxInputReportLength()} " +
                         $"out={d.GetMaxOutputReportLength()} feature={d.GetMaxFeatureReportLength()} rel={d.ReleaseNumberBcd:X4}");
        }
        foreach (var d in devices)
        {
            if (!d.TryOpen(out HidStream stream)) continue;
            _stream = stream;
            _buffer = new byte[Math.Max(64, d.GetMaxInputReportLength())];
            Log.Line("kraken: opened (shared with FanControl)");
            return true;
        }
        LastError = "found the Kraken but could not open it";
        return false;
    }

    static string ProductName(HidDevice d)
    {
        try { return d.GetProductName(); } catch (Exception ex) { return $"? ({ex.Message})"; }
    }

    void Close()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
    }

    public void Dispose() => Close();
}
