using Amazon.CDK;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECS;
using Amazon.CDK.AWS.ECS.Patterns;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.SecretsManager;
using Constructs;
using EcsSecret = Amazon.CDK.AWS.ECS.Secret;
using SmSecret = Amazon.CDK.AWS.SecretsManager.Secret;

namespace ApiForge.Cdk;

public class ApiForgeStack : Stack
{
    public ApiForgeStack(Construct scope, string id, bool isProd, IStackProps? props = null)
        : base(scope, id, props)
    {
        // ----- Network: no NAT. Public subnet for the ALB, isolated subnets for ECS + RDS. -----
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = isProd ? 2 : 2, // 2 AZs are required for an ALB and an RDS subnet group
            NatGateways = 0,
            SubnetConfiguration = new[]
            {
                new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
                new SubnetConfiguration { Name = "isolated", SubnetType = SubnetType.PRIVATE_ISOLATED, CidrMask = 24 }
            }
        });

        // VPC endpoints so isolated tasks can pull images and read secrets without a NAT.
        vpc.AddInterfaceEndpoint("EcrApi", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.ECR });
        vpc.AddInterfaceEndpoint("EcrDkr", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.ECR_DOCKER });
        vpc.AddInterfaceEndpoint("Secrets", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.SECRETS_MANAGER });
        vpc.AddInterfaceEndpoint("Logs", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS });
        vpc.AddGatewayEndpoint("S3", new GatewayVpcEndpointOptions { Service = GatewayVpcEndpointAwsService.S3 }); // ECR layers

        // ----- Secrets -------------------------------------------------------------------------
        var dbSecret = new DatabaseSecret(this, "DbSecret", new DatabaseSecretProps { Username = "apiforge" });

        var jwtSecret = new SmSecret(this, "JwtKey", new SecretProps
        {
            GenerateSecretString = new SecretStringGenerator
            {
                PasswordLength = 48,
                ExcludePunctuation = true
            }
        });

        // ----- Database ------------------------------------------------------------------------
        var db = new DatabaseInstance(this, "Db", new DatabaseInstanceProps
        {
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps
            {
                Version = PostgresEngineVersion.VER_16
            }),
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED },
            Credentials = Credentials.FromSecret(dbSecret),
            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(InstanceClass.BURSTABLE3, InstanceSize.MICRO),
            AllocatedStorage = 20,
            MultiAz = isProd,
            // Learning-friendly lifecycle: dev is destroyable, prod is protected.
            DeletionProtection = isProd,
            DeleteAutomatedBackups = !isProd,
            RemovalPolicy = isProd ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY
        });

        // ----- ECS Fargate behind a public ALB -------------------------------------------------
        var cluster = new Cluster(this, "Cluster", new ClusterProps { Vpc = vpc });

        var service = new ApplicationLoadBalancedFargateService(this, "Service",
            new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                Cpu = 256,
                MemoryLimitMiB = 512,
                DesiredCount = 1,
                MinHealthyPercent = 100,
                // Fail (and roll back) a bad deploy in minutes instead of hanging for hours.
                CircuitBreaker = new DeploymentCircuitBreaker { Rollback = true },
                PublicLoadBalancer = true,
                AssignPublicIp = false,
                TaskSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED },
                TaskImageOptions = new ApplicationLoadBalancedTaskImageOptions
                {
                    // Builds the Docker image from the repo root and publishes it via CDK assets —
                    // no manual ECR push, no first-deploy chicken-and-egg.
                    Image = ContainerImage.FromAsset("..", new AssetImageProps
                    {
                        File = "Dockerfile"
                    }),
                    ContainerPort = 8080,
                    Environment = new Dictionary<string, string>
                    {
                        ["ASPNETCORE_ENVIRONMENT"] = "Production",
                        ["Jwt__Issuer"] = "ApiForge",
                        ["Jwt__Audience"] = "ApiForgeClients"
                    },
                    // ▶ DB injected as individual fields (not the whole JSON blob).
                    Secrets = new Dictionary<string, EcsSecret>
                    {
                        ["DB_HOST"] = EcsSecret.FromSecretsManager(dbSecret, "host"),
                        ["DB_PORT"] = EcsSecret.FromSecretsManager(dbSecret, "port"),
                        ["DB_NAME"] = EcsSecret.FromSecretsManager(dbSecret, "dbname"),
                        ["DB_USER"] = EcsSecret.FromSecretsManager(dbSecret, "username"),
                        ["DB_PASSWORD"] = EcsSecret.FromSecretsManager(dbSecret, "password"),
                        ["Jwt__Key"] = EcsSecret.FromSecretsManager(jwtSecret)
                    },
                    LogDriver = LogDriver.AwsLogs(new AwsLogDriverProps
                    {
                        StreamPrefix = "apiforge",
                        LogRetention = RetentionDays.ONE_MONTH
                    })
                }
            });

        // ALB health check hits the dedicated endpoint, not "/".
        service.TargetGroup.ConfigureHealthCheck(new Amazon.CDK.AWS.ElasticLoadBalancingV2.HealthCheck
        {
            Path = "/health",
            HealthyHttpCodes = "200"
        });

        // Least privilege: only the two secrets the app actually reads.
        dbSecret.GrantRead(service.TaskDefinition.TaskRole);
        jwtSecret.GrantRead(service.TaskDefinition.TaskRole);

        // Let the API tasks reach Postgres.
        db.Connections.AllowDefaultPortFrom(service.Service);

        // ----- Cost guardrail: email if the monthly spend crosses the threshold. ---------------
        var budgetEmail = (Node.TryGetContext("budgetEmail") as string);
        if (!string.IsNullOrWhiteSpace(budgetEmail))
        {
            new CfnBudget(this, "MonthlyBudget", new CfnBudgetProps
            {
                Budget = new CfnBudget.BudgetDataProperty
                {
                    BudgetType = "COST",
                    TimeUnit = "MONTHLY",
                    BudgetLimit = new CfnBudget.SpendProperty { Amount = isProd ? 50 : 10, Unit = "USD" }
                },
                NotificationsWithSubscribers = new[]
                {
                    new CfnBudget.NotificationWithSubscribersProperty
                    {
                        Notification = new CfnBudget.NotificationProperty
                        {
                            NotificationType = "ACTUAL",
                            ComparisonOperator = "GREATER_THAN",
                            Threshold = 80,
                            ThresholdType = "PERCENTAGE"
                        },
                        Subscribers = new[]
                        {
                            new CfnBudget.SubscriberProperty { SubscriptionType = "EMAIL", Address = budgetEmail }
                        }
                    }
                }
            });
        }

        Amazon.CDK.Tags.Of(this).Add("project", "apiforge");
        Amazon.CDK.Tags.Of(this).Add("env", isProd ? "production" : "dev");

        new CfnOutput(this, "ApiUrl", new CfnOutputProps { Value = service.LoadBalancer.LoadBalancerDnsName });
    }
}
