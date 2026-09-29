using JiranisokoTech.Tests.Identity;
using JiranisokoTech.Tests.Postgres;
using Microsoft.AspNetCore.Hosting;

namespace JiranisokoTech.Tests.Workflows;

/// <summary>
/// What a workflow test needs from the host that ordinary page tests do not.
/// </summary>
/// <remarks>
/// A GitHub webhook secret, because the delivery chain connects a repository and then signs
/// deliveries with it. And background loops slowed to an hour: the outbox and the webhook inbox
/// are each drained by a hosted service every few seconds, and a test that also drains them
/// by hand would be racing a second worker for the same rows — passing or failing on timing,
/// which is the one kind of test worse than none. The test runs each dispatcher itself, at
/// the moment the chain needs it.
/// </remarks>
public static class Workflow
{
    public const string GitHubSecret = "the-secret-the-workflow-repository-holds";

    public static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Git:Providers:GitHub:Secret", GitHubSecret);
        builder.UseSetting("Git:PollInterval", "01:00:00");
        builder.UseSetting("Outbox:PollInterval", "01:00:00");

        // Letters are part of the chains but not what these tests check, and the file
        // transport would write them into the test run's working directory.
        builder.UseSetting("Mail:Transport", "None");
    }
}

public sealed class WorkflowFactory : ApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Workflow.Configure(builder);
    }
}

public sealed class WorkflowPostgresFactory : PostgresApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Workflow.Configure(builder);
    }
}
