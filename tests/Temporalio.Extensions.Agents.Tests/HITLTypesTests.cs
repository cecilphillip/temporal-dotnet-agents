using System.Text.Json;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests that the HITL approval types serialize/deserialize correctly and have expected defaults.
/// </summary>
public class HITLTypesTests
{
    [Fact]
    public void ApprovalRequest_DefaultRequestId_IsNonEmpty()
    {
        var request = new ApprovalRequest { Action = "Test" };
        Assert.NotEmpty(request.RequestId);
    }

    [Fact]
    public void ApprovalRequest_RoundTripsViaJson()
    {
        var original = new ApprovalRequest
        {
            RequestId = "req-123",
            Action = "Delete all records",
            Details = "This is irreversible."
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<ApprovalRequest>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.Equal("Delete all records", deserialized.Action);
        Assert.Equal("This is irreversible.", deserialized.Details);
    }

    [Fact]
    public void ApprovalDecision_RoundTripsViaJson()
    {
        var decision = new ApprovalDecision
        {
            RequestId = "req-123",
            Approved = true,
            Comment = "Looks good."
        };

        var json = JsonSerializer.Serialize(decision);
        var deserialized = JsonSerializer.Deserialize<ApprovalDecision>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.True(deserialized.Approved);
        Assert.Equal("Looks good.", deserialized.Comment);
    }

    [Fact]
    public void ApprovalTicket_RoundTripsViaJson()
    {
        var ticket = new ApprovalTicket
        {
            RequestId = "req-456",
            Approved = false,
            Comment = "Rejected: missing justification."
        };

        var json = JsonSerializer.Serialize(ticket);
        var deserialized = JsonSerializer.Deserialize<ApprovalTicket>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("req-456", deserialized.RequestId);
        Assert.False(deserialized.Approved);
        Assert.Equal("Rejected: missing justification.", deserialized.Comment);
    }

    [Fact]
    public void ApprovalDecision_RejectedWithoutComment_SerializesNullComment()
    {
        var decision = new ApprovalDecision { RequestId = "req-789", Approved = false };
        var json = JsonSerializer.Serialize(decision);
        var deserialized = JsonSerializer.Deserialize<ApprovalDecision>(json);

        Assert.Null(deserialized?.Comment);
    }
}
