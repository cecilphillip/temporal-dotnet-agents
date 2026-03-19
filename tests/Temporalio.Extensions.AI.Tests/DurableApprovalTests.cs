using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.AI.Tests;

public class DurableApprovalTests
{
    [Fact]
    public void DurableApprovalRequest_Properties()
    {
        var request = new DurableApprovalRequest
        {
            RequestId = "req-1",
            FunctionName = "delete_records",
            CallId = "call-42",
            Description = "Deletes all user records",
        };

        Assert.Equal("req-1", request.RequestId);
        Assert.Equal("delete_records", request.FunctionName);
        Assert.Equal("call-42", request.CallId);
        Assert.Equal("Deletes all user records", request.Description);
    }

    [Fact]
    public void DurableApprovalDecision_Approved()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-1",
            Approved = true,
            Reason = "Looks good",
        };

        Assert.Equal("req-1", decision.RequestId);
        Assert.True(decision.Approved);
        Assert.Equal("Looks good", decision.Reason);
    }

    [Fact]
    public void DurableApprovalDecision_Rejected()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-1",
            Approved = false,
            Reason = "Too dangerous",
        };

        Assert.False(decision.Approved);
    }

    [Fact]
    public void DurableApprovalRequest_RoundTrips()
    {
        var request = new DurableApprovalRequest
        {
            RequestId = "req-42",
            FunctionName = "deploy",
            CallId = "call-7",
            Description = "Deploy to production",
        };

        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("req-42", deserialized!.RequestId);
        Assert.Equal("deploy", deserialized.FunctionName);
        Assert.Equal("call-7", deserialized.CallId);
        Assert.Equal("Deploy to production", deserialized.Description);
    }

    [Fact]
    public void DurableApprovalDecision_RoundTrips()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-42",
            Approved = true,
            Reason = "LGTM",
        };

        var json = JsonSerializer.Serialize(decision, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalDecision>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("req-42", deserialized!.RequestId);
        Assert.True(deserialized.Approved);
        Assert.Equal("LGTM", deserialized.Reason);
    }

    [Fact]
    public void DurableExecutionOptions_ApprovalTimeout_Default()
    {
        var options = new DurableExecutionOptions();
        Assert.Equal(TimeSpan.FromDays(7), options.ApprovalTimeout);
    }
}
