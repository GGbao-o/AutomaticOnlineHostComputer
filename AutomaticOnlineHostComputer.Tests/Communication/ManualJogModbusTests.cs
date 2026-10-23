using System.Net;
using System.Net.Sockets;
using AutomaticOnlineHostComputer.Communication.Clients;
using AutomaticOnlineHostComputer.Communication.DeviceServices;

namespace AutomaticOnlineHostComputer.Tests.Communication;

/// <summary>本地TCP模拟PLC，验证实际FC05报文与回显/回读校验，不接触现场网络。</summary>
public sealed class ManualJogModbusTests
{
    [Theory]
    [InlineData(70, true)] [InlineData(71, true)] [InlineData(72, true)]
    [InlineData(73, false)] [InlineData(74, false)] [InlineData(75, false)]
    public async Task Write_coil_uses_fc05_and_the_exact_zero_based_address(int address, bool on)
    {
        await ExchangeOnce(async client => await client.WriteCoilAsync(address, on), request =>
        {
            Assert.Equal(new byte[] { 0x05, 0, (byte)address, on ? (byte)0xFF : (byte)0, 0 }, request);
            return request;
        });
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(3)]
    public async Task Wrong_function_address_or_value_in_echo_is_rejected(int byteToChange)
    {
        await ExchangeOnce(client => Assert.ThrowsAsync<IOException>(() => client.WriteCoilAsync(70, true)), request =>
        {
            request[byteToChange] ^= 1;
            return request;
        });
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(3, 0)] [InlineData(1, 0)]
    public async Task Invalid_or_still_on_readback_is_not_silently_converted_to_off(int function, int count)
    {
        await ExchangeOnce(async client =>
        {
            if (function == 1 && count == 1)
                Assert.True(await client.ReadCoilCheckedAsync(70));
            else
                await Assert.ThrowsAsync<IOException>(() => client.ReadCoilCheckedAsync(70));
        }, request =>
        {
            Assert.Equal(new byte[] { 1, 0, 70, 0, 1 }, request);
            return new byte[] { (byte)function, (byte)count, 1 };
        });
    }

    [Fact]
    public async Task Service_sends_only_manual_enable_then_one_on_one_off_and_read()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var requests = new List<byte[]>();
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            for (int i = 0; i < 5; i++)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, timeout.Token);
                var pdu = new byte[((header[4] << 8) | header[5]) - 1];
                await stream.ReadExactlyAsync(pdu, timeout.Token);
                requests.Add(pdu);
                await Reply(stream, header, pdu[0] == 1 ? new byte[] { 1, 1, 0 } : pdu, timeout.Token);
            }
        });
        var service = new CraneService("本地JOG测试", "127.0.0.1", port);
        try
        {
            await service.JogPulseAsync(70, 1, timeout.Token);
            await server.WaitAsync(timeout.Token);
            Assert.Equal(new byte[] { 6, 0x0F, 0xA1, 0, 2 }, requests[0]); // D4001=2
            Assert.Equal(new byte[] { 6, 0x11, 0x94, 0, 2 }, requests[1]); // D4500=2
            Assert.Equal(new byte[] { 5, 0, 70, 0xFF, 0 }, requests[2]);
            Assert.Equal(new byte[] { 5, 0, 70, 0, 0 }, requests[3]);
            Assert.Equal(new byte[] { 1, 0, 70, 0, 1 }, requests[4]);
        }
        finally { timeout.Cancel(); listener.Stop(); await service.DisconnectAsync(); }
    }

    private static async Task ExchangeOnce(Func<ModbusTcpClient, Task> action, Func<byte[], byte[]> response)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task server = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = peer.GetStream();
            var header = new byte[7];
            await stream.ReadExactlyAsync(header, timeout.Token);
            var pdu = new byte[((header[4] << 8) | header[5]) - 1];
            await stream.ReadExactlyAsync(pdu, timeout.Token);
            await Reply(stream, header, response(pdu), timeout.Token);
        });
        await using var client = new ModbusTcpClient("127.0.0.1", port);
        try
        {
            await client.ConnectAsync(timeout.Token);
            await action(client).WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
        }
        finally { timeout.Cancel(); listener.Stop(); }
    }

    private static async Task Reply(NetworkStream stream, byte[] requestHeader, byte[] body, CancellationToken ct)
    {
        byte[] frame = new byte[7 + body.Length];
        Array.Copy(requestHeader, frame, 7);
        frame[4] = (byte)((body.Length + 1) >> 8);
        frame[5] = (byte)(body.Length + 1);
        Array.Copy(body, 0, frame, 7, body.Length);
        await stream.WriteAsync(frame, ct);
    }
}
