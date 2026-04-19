using LaneZstd.Cli;

namespace LaneZstd.Tests;

public sealed class TrafficPayloadFactoryTests
{
    [Fact]
    public void Create_KeepsPayloadWithinConfiguredBounds()
    {
        const int minPayloadBytes = 50;
        const int maxPayloadBytes = 1186;

        for (var sequence = 0; sequence < 256; sequence++)
        {
            var payload = TrafficPayloadFactory.Create(
                direction: sequence % 2 == 0 ? "edge->hub" : "hub->edge",
                sequence,
                seed: 424242,
                averagePayloadBytes: 700,
                minPayloadBytes,
                maxPayloadBytes);

            Assert.InRange(payload.Bytes.Length, minPayloadBytes, maxPayloadBytes);
        }
    }
}
