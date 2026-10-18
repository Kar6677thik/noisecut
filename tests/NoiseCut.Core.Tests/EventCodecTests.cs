using System.Text;
using NoiseCut.Core.Events;
using NoiseCut.Core.Tests.TestSupport;

namespace NoiseCut.Core.Tests;

public class EventCodecTests
{
    [Fact]
    public void Round_trips_an_event()
    {
        var evt = Ev.Delayed("o-1", Ev.At(3), minutes: 18);

        Assert.True(EventCodec.TryParse(EventCodec.Serialize(evt), out var parsed, out var error), error);

        Assert.Equal(evt.EventId, parsed!.EventId);
        Assert.Equal(evt.OccurredAt, parsed.OccurredAt);
        Assert.Equal(18, (int)parsed.Data!["delayMinutes"]!);
        Assert.Equal("kitchen", (string)parsed.Data!["stage"]!);
    }

    [Fact]
    public void Serializes_with_the_spec_field_names()
    {
        var json = Encoding.UTF8.GetString(EventCodec.Serialize(Ev.Delayed("o-1", Ev.At(0))));

        Assert.Contains("\"eventId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"order.delayed\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"occurredAt\":\"2026-10-17T12:00:00+00:00\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("isTick", json, StringComparison.Ordinal); // computed property, not part of the envelope
    }

    [Fact]
    public void Parses_the_example_from_the_spec()
    {
        const string json = """
            { "eventId": "0192f6c1-7d1e-7b3a-9c41-2f0e1a7b9c11", "type": "order.delayed", "schemaVersion": 1,
              "occurredAt": "2026-10-17T19:42:10.123Z", "orderId": "o-000184213", "restaurantId": "r-017",
              "zoneId": "z-02", "data": { "delayMinutes": 18, "stage": "kitchen" } }
            """;

        Assert.True(EventCodec.TryParse(Encoding.UTF8.GetBytes(json), out var evt, out _));
        Assert.Equal("r-017", evt!.RestaurantId);
    }

    [Fact]
    public void Ticks_need_no_order_fields()
    {
        Assert.True(EventCodec.TryParse(EventCodec.Serialize(Ev.Tick(Ev.At(1))), out var tick, out _));
        Assert.True(tick!.IsTick);
    }

    [Theory]
    [InlineData("not json", "invalid json")]
    [InlineData("""{"eventId":"0192f6c1-7d1e-7b3a-9c41-2f0e1a7b9c11","type":"order.teleported","occurredAt":"2026-10-17T19:42:10Z","orderId":"o","restaurantId":"r","zoneId":"z"}""", "unknown event type")]
    [InlineData("""{"eventId":"0192f6c1-7d1e-7b3a-9c41-2f0e1a7b9c11","type":"order.placed","schemaVersion":2,"occurredAt":"2026-10-17T19:42:10Z","orderId":"o","restaurantId":"r","zoneId":"z"}""", "unsupported schemaVersion")]
    [InlineData("""{"eventId":"0192f6c1-7d1e-7b3a-9c41-2f0e1a7b9c11","type":"order.placed","occurredAt":"2026-10-17T19:42:10Z"}""", "need orderId")]
    [InlineData("""{"type":"order.placed","occurredAt":"2026-10-17T19:42:10Z","orderId":"o","restaurantId":"r","zoneId":"z"}""", "eventId")]
    [InlineData("null", "empty record")]
    public void Rejects_poison_messages_with_a_reason(string json, string expectedError)
    {
        Assert.False(EventCodec.TryParse(Encoding.UTF8.GetBytes(json), out var evt, out var error));
        Assert.Null(evt);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Field_access_reads_envelope_and_data_fields()
    {
        var evt = Ev.Delayed("o-9", Ev.At(0), minutes: 16);

        Assert.Equal("o-9", EventFields.GetString(evt, "orderId"));
        Assert.Equal("r-003", EventFields.GetString(evt, "restaurantId"));
        Assert.Equal("kitchen", EventFields.GetString(evt, "data.stage"));
        Assert.Equal("16", EventFields.GetString(evt, "data.delayMinutes"));
        Assert.Null(EventFields.GetString(evt, "data.missing"));
    }
}
