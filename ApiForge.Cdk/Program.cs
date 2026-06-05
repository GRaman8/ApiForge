using Amazon.CDK;

var app = new App();

// Environment selector: cdk deploy --context env=dev|prod  (defaults to dev).
var env = app.Node.TryGetContext("env") as string ?? "dev";
var isProd = string.Equals(env, "prod", StringComparison.OrdinalIgnoreCase);

new ApiForge.Cdk.ApiForgeStack(app, $"ApiForge-{env}", isProd, new StackProps
{
    // Uses the account/region from your AWS CLI profile / env vars.
    Env = new Amazon.CDK.Environment
    {
        Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
        Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION")
    }
});

app.Synth();
