using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.Terraform;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Tests.Extractors;

public class TerraformExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static async Task<ExtractionResult> ExtractAsync(string content, string filePath = "/test/main.tf")
    {
        var extractor = new TerraformExtractor(NullLogger<TerraformExtractor>.Instance);
        return await extractor.ExtractAsync(filePath, content, TestContext);
    }

    [Fact]
    public async Task SupportedExtensions_IncludesTfAndTfvars()
    {
        var extractor = new TerraformExtractor(NullLogger<TerraformExtractor>.Instance);
        extractor.SupportedExtensions.ShouldContain(".tf");
        extractor.SupportedExtensions.ShouldContain(".tfvars");
    }

    // ── Resource extraction ──────────────────────────────────────────

    [Fact]
    public async Task Extracts_Resource()
    {
        var tf = """
            resource "aws_instance" "web" {
              ami           = "ami-123456"
              instance_type = "t2.micro"
            }
            """;

        var result = await ExtractAsync(tf);

        var node = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformResource && n.Name == "aws_instance.web");
        node.Properties["resource_type"].ShouldBe("aws_instance");
        node.Properties["provider"].ShouldBe("aws");
    }

    [Fact]
    public async Task Extracts_MultipleResources()
    {
        var tf = """
            resource "aws_instance" "web" {
              ami = "ami-123"
            }

            resource "aws_security_group" "web_sg" {
              name = "web-sg"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Nodes.Count(n => n.Label == NodeLabel.TerraformResource).ShouldBe(2);
    }

    // ── Data source extraction ───────────────────────────────────────

    [Fact]
    public async Task Extracts_DataSource()
    {
        var tf = """
            data "aws_ami" "ubuntu" {
              most_recent = true
              filter {
                name   = "name"
                values = ["ubuntu/images/*"]
              }
            }
            """;

        var result = await ExtractAsync(tf);

        var node = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformDataSource);
        node.Name.ShouldBe("data.aws_ami.ubuntu");
        node.Properties["provider"].ShouldBe("aws");
    }

    // ── Module extraction ────────────────────────────────────────────

    [Fact]
    public async Task Extracts_Module_WithSource()
    {
        var tf = """
            module "vpc" {
              source  = "terraform-aws-modules/vpc/aws"
              version = "3.0.0"

              cidr = "10.0.0.0/16"
            }
            """;

        var result = await ExtractAsync(tf);

        var node = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformModule && n.Name == "vpc");
        node.Properties["source"].ShouldBe("terraform-aws-modules/vpc/aws");
        node.Properties["version"].ShouldBe("3.0.0");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_MODULE &&
            e.TargetQN == "terraform_registry:terraform-aws-modules/vpc/aws");
    }

    [Fact]
    public async Task Extracts_Module_WithLocalSource()
    {
        var tf = """
            module "app" {
              source = "./modules/app"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_MODULE &&
            e.TargetQN == "TestProject.module_source.modules_app");
    }

    // ── Variable extraction ──────────────────────────────────────────

    [Fact]
    public async Task Extracts_Variables()
    {
        var tf = """
            variable "region" {
              type        = string
              default     = "us-east-1"
              description = "AWS region"
            }

            variable "instance_count" {
              type    = number
              default = 2
            }
            """;

        var result = await ExtractAsync(tf);

        result.Nodes.Count(n => n.Label == NodeLabel.TerraformVariable).ShouldBe(2);

        var region = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformVariable && n.Name == "region");
        region.Properties["description"].ShouldBe("AWS region");
        region.Properties["default_value"].ShouldBe("us-east-1");
    }

    // ── Output extraction ────────────────────────────────────────────

    [Fact]
    public async Task Extracts_Outputs()
    {
        var tf = """
            output "instance_ip" {
              description = "Public IP of the instance"
              value       = aws_instance.web.public_ip
            }
            """;

        var result = await ExtractAsync(tf);

        var node = result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformOutput && n.Name == "instance_ip");
        node.Properties["description"].ShouldBe("Public IP of the instance");

        // Output references aws_instance.web → edge
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CALLS &&
            e.TargetQN == "TestProject.resource.aws_instance.web");
    }

    // ── Locals extraction ────────────────────────────────────────────

    [Fact]
    public async Task Extracts_Locals()
    {
        var tf = """
            locals {
              common_tags = {
                Environment = "prod"
              }
              app_name = "orders"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformVariable &&
            n.Name == "common_tags" &&
            n.Properties["scope"].ToString() == "local");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformVariable &&
            n.Name == "app_name" &&
            n.Properties["scope"].ToString() == "local");
    }

    // ── depends_on extraction ────────────────────────────────────────

    [Fact]
    public async Task Extracts_DependsOn()
    {
        var tf = """
            resource "aws_instance" "web" {
              ami           = "ami-123"
              instance_type = "t2.micro"

              depends_on = [aws_security_group.web_sg, aws_subnet.main]
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPENDS_ON &&
            e.SourceQN == "TestProject.resource.aws_instance.web" &&
            e.TargetQN == "TestProject.resource.aws_security_group.web_sg");
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPENDS_ON &&
            e.TargetQN == "TestProject.resource.aws_subnet.main");
    }

    // ── Cross-repo edges ─────────────────────────────────────────────

    [Fact]
    public async Task Extracts_EcsDeploy()
    {
        var tf = """
            resource "aws_ecs_service" "orders" {
              name            = "orders-api"
              cluster         = aws_ecs_cluster.main.id
              task_definition = aws_ecs_task_definition.orders.arn
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["container"].ToString() == "orders-api");
    }

    [Fact]
    public async Task Extracts_LambdaDeploy()
    {
        var tf = """
            resource "aws_lambda_function" "processor" {
              function_name = "order-processor"
              runtime       = "dotnet8"
              handler       = "OrderProcessor::Handler"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["function_name"].ToString() == "order-processor");
    }

    [Fact]
    public async Task Extracts_SqsQueue()
    {
        var tf = """
            resource "aws_sqs_queue" "orders" {
              name = "order-processing-queue"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CONFIGURES &&
            e.Properties!["queue_name"].ToString() == "order-processing-queue");
    }

    [Fact]
    public async Task Extracts_RdsDatabase()
    {
        var tf = """
            resource "aws_db_instance" "orders" {
              engine    = "mysql"
              db_name   = "orders_db"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CONFIGURES &&
            e.Properties!["database"].ToString() == "orders_db");

        var node = result.Nodes.First(n => n.Label == NodeLabel.TerraformResource);
        node.Properties["engine"].ShouldBe("mysql");
    }

    [Fact]
    public async Task Extracts_S3Bucket()
    {
        var tf = """
            resource "aws_s3_bucket" "artifacts" {
              bucket = "company-build-artifacts"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CONFIGURES &&
            e.Properties!["bucket"].ToString() == "company-build-artifacts");
    }

    [Fact]
    public async Task Extracts_AppServiceDeploy()
    {
        var tf = """
            resource "azurerm_windows_web_app" "orders" {
              name = "orders-api-prod"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["app_service"].ToString() == "orders-api-prod");
    }

    // ── Tfvars extraction ────────────────────────────────────────────

    [Fact]
    public async Task Extracts_Tfvars()
    {
        var tfvars = """
            region         = "us-east-1"
            instance_count = 3
            db_host        = "mysql.internal:3306"
            """;

        var result = await ExtractAsync(tfvars, "/test/prod.tfvars");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformVariable && n.Name == "region");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformVariable &&
            n.Name == "db_host" &&
            n.Properties.ContainsKey("likely_service_ref"));
    }

    // ── Static reference analysis ────────────────────────────────────

    [Fact]
    public async Task ScansForUrls_InStringValues()
    {
        var tf = """
            resource "aws_lambda_function" "webhook" {
              environment {
                variables = {
                  API_URL = "http://orders-api.internal:5000/api"
                }
              }
            }
            """;

        var result = await ExtractAsync(tf);

        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "terraform_url_ref" &&
            c.CalleeName.Contains("orders-api.internal"));
    }

    [Fact]
    public async Task ScansForHostnames_InStringValues()
    {
        var tf = """
            variable "db_endpoint" {
              default = "database.internal"
            }
            """;

        var result = await ExtractAsync(tf);

        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "terraform_host_ref" &&
            c.CalleeName == "database.internal");
    }

    // ── Metadata ─────────────────────────────────────────────────────

    [Fact]
    public async Task Extracts_Metadata()
    {
        var tf = """
            resource "aws_instance" "web" {
              ami = "ami-123"
            }
            """;

        var result = await ExtractAsync(tf);

        result.Metadata.ShouldNotBeNull();
        result.Metadata.Language.ShouldBe("HCL");
        result.Metadata.Framework.ShouldBe("Terraform");
    }

    // ── Nested blocks ────────────────────────────────────────────────

    [Fact]
    public async Task HandlesNestedBlocks()
    {
        var tf = """
            resource "aws_ecs_task_definition" "app" {
              family = "app-task"
              container_definitions = jsonencode([{
                name  = "app-container"
                image = "registry.internal/my-app:latest"
                portMappings = [{
                  containerPort = 8080
                }]
              }])
            }
            """;

        var result = await ExtractAsync(tf);

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.TerraformResource &&
            n.Name == "aws_ecs_task_definition.app");
    }

    [Fact]
    public async Task ReturnsEmpty_ForEmptyContent()
    {
        var result = await ExtractAsync("", "/test/empty.tf");
        result.Nodes.ShouldBeEmpty();
    }
}
