// Quick handshake: connect RFCOMM ch15, ask battery, expect 0x4007/0xE001.

using NothingBuds.Bluetooth;

namespace NothingBuds.Services;

public static class NothingProbe
{
    public static async Task<bool> IsNothingDeviceAsync(string mac, int timeoutMs = 4500)
    {
        using var rf = new RfcommClient();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ushort cmd, byte[] _) {
            if (cmd == Cmd.RespBattery || cmd == Cmd.EventBattery)
                tcs.TrySetResult(true);
        }
        rf.FrameReceived += Handler;
        try
        {
            await Task.Run(() => rf.Connect(mac, 15, Math.Min(timeoutMs, 4000)));
            rf.Send(Cmd.Battery);
            using var cts = new CancellationTokenSource(timeoutMs);
            using (cts.Token.Register(() => tcs.TrySetResult(false)))
                return await tcs.Task;
        }
        catch { return false; }
    }
}
