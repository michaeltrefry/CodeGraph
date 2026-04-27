using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.Ansible;
using CodeGraph.Models;

namespace CodeGraph.Tests.Extractors;

public class AnsibleExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static async Task<ExtractionResult> ExtractAsync(string content, string filePath)
    {
        var extractor = new AnsibleExtractor(NullLogger<AnsibleExtractor>.Instance);
        return await extractor.ExtractAsync(filePath, content, TestContext);
    }

    [Fact]
    public async Task SupportedExtensions_IncludesYmlAndYaml()
    {
        var extractor = new AnsibleExtractor(NullLogger<AnsibleExtractor>.Instance);
        extractor.SupportedExtensions.ShouldContain(".yml");
        extractor.SupportedExtensions.ShouldContain(".yaml");
    }

    [Fact]
    public async Task ReturnsEmpty_ForNonAnsibleYaml()
    {
        var yaml = """
            name: my-package
            version: 1.0.0
            dependencies:
              lodash: ^4.0.0
            """;

        var result = await ExtractAsync(yaml, "/test/package.yml");

        result.Nodes.ShouldBeEmpty();
        result.Edges.ShouldBeEmpty();
    }

    // -- Playbook extraction ------------------------------------------

    [Fact]
    public async Task Extracts_Playbook_WithRoles()
    {
        var yaml = """
            - name: Deploy web servers
              hosts: webservers
              roles:
                - nginx
                - app_deploy
            """;

        var result = await ExtractAsync(yaml, "/test/deploy.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Playbook && n.Name == "deploy");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Role && n.Name == "nginx");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Role && n.Name == "app_deploy");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_ROLE &&
            e.TargetQN == "TestProject.role.nginx");
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_ROLE &&
            e.TargetQN == "TestProject.role.app_deploy");
    }

    [Fact]
    public async Task Extracts_Playbook_WithInlineTasks()
    {
        var yaml = """
            - name: Setup server
              hosts: all
              tasks:
                - name: Install packages
                  apt:
                    name: nginx
                    state: present
                - name: Start nginx
                  service:
                    name: nginx
                    state: started
            """;

        var result = await ExtractAsync(yaml, "/test/setup.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleTask && n.Name == "Install packages");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleTask && n.Name == "Start nginx");

        // Service task should create DEPLOYS edge
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["service_name"].ToString() == "nginx");
    }

    [Fact]
    public async Task Extracts_Playbook_WithRoleMapping()
    {
        var yaml = """
            - name: Deploy with params
              hosts: appservers
              roles:
                - role: app_deploy
                  vars:
                    app_version: "2.0"
            """;

        var result = await ExtractAsync(yaml, "/test/deploy.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Role && n.Name == "app_deploy");
    }

    // -- Task extraction ----------------------------------------------

    [Fact]
    public async Task Extracts_Tasks_WithNotify()
    {
        var yaml = """
            - name: Update config
              template:
                src: app.conf.j2
                dest: /etc/app/app.conf
              notify: restart app
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/tasks/main.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleTask && n.Name == "Update config");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.NOTIFIES_HANDLER &&
            e.TargetQN == "TestProject.handler.restart_app");

        // Template -> CONFIGURES edge
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CONFIGURES &&
            e.Properties!["destination"].ToString() == "/etc/app/app.conf");
    }

    [Fact]
    public async Task Extracts_Tasks_WithMultipleNotify()
    {
        var yaml = """
            - name: Update config
              copy:
                src: app.conf
                dest: /etc/app/app.conf
              notify:
                - restart app
                - reload nginx
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/tasks/main.yml");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.NOTIFIES_HANDLER &&
            e.TargetQN == "TestProject.handler.restart_app");
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.NOTIFIES_HANDLER &&
            e.TargetQN == "TestProject.handler.reload_nginx");
    }

    [Fact]
    public async Task Extracts_Tasks_WithIncludeRole()
    {
        var yaml = """
            - name: Include common role
              include_role:
                name: common
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/tasks/main.yml");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_ROLE &&
            e.TargetQN == "TestProject.role.common");
    }

    [Fact]
    public async Task Extracts_Tasks_WithBlock()
    {
        var yaml = """
            - block:
                - name: Install package
                  apt:
                    name: nginx
              rescue:
                - name: Log failure
                  debug:
                    msg: "Install failed"
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/tasks/main.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleTask && n.Name == "Install package");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleTask && n.Name == "Log failure");
    }

    // -- Handler extraction ------------------------------------------

    [Fact]
    public async Task Extracts_Handlers()
    {
        var yaml = """
            - name: restart app
              service:
                name: myapp
                state: restarted

            - name: reload nginx
              service:
                name: nginx
                state: reloaded
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/handlers/main.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleHandler && n.Name == "restart app");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleHandler && n.Name == "reload nginx");

        // Handlers with service module -> DEPLOYS
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["service_name"].ToString() == "myapp");
    }

    // -- Variable extraction -----------------------------------------

    [Fact]
    public async Task Extracts_DefaultVariables()
    {
        var yaml = """
            app_port: 8080
            app_name: myservice
            db_host: mysql.internal:3306
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/defaults/main.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleVariable &&
            n.Name == "app_port" &&
            n.Properties["scope"].ToString() == "default");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleVariable &&
            n.Name == "db_host" &&
            n.Properties.ContainsKey("likely_service_ref"));
    }

    [Fact]
    public async Task Extracts_RoleVars()
    {
        var yaml = """
            log_level: info
            max_retries: 3
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/vars/main.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.AnsibleVariable &&
            n.Name == "log_level" &&
            n.Properties["scope"].ToString() == "var");
    }

    // -- Role meta extraction ----------------------------------------

    [Fact]
    public async Task Extracts_RoleDependencies()
    {
        var yaml = """
            dependencies:
              - common
              - role: logging
              - name: monitoring
            galaxy_info:
              description: Deploys the main application
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/meta/main.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Role && n.Name == "myapp");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_ROLE &&
            e.TargetQN == "TestProject.role.common");
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_ROLE &&
            e.TargetQN == "TestProject.role.logging");
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INCLUDES_ROLE &&
            e.TargetQN == "TestProject.role.monitoring");
    }

    // -- Requirements file -------------------------------------------

    [Fact]
    public async Task Extracts_Requirements()
    {
        var yaml = """
            - name: geerlingguy.docker
              src: geerlingguy.docker
              version: "6.0.0"
            - name: internal_role
            """;

        var result = await ExtractAsync(yaml, "/test/requirements.yml");

        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Role && n.Name == "geerlingguy.docker");
        result.Nodes.ShouldContain(n =>
            n.Label == NodeLabel.Role && n.Name == "internal_role");
    }

    // -- Cross-repo edges --------------------------------------------

    [Fact]
    public async Task Extracts_HttpCalls_FromUriModule()
    {
        var yaml = """
            - name: Health check
              hosts: all
              tasks:
                - name: Check API health
                  uri:
                    url: http://api.internal:5000/health
                    method: GET
            """;

        var result = await ExtractAsync(yaml, "/test/healthcheck.yml");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.HTTP_CALLS &&
            e.Properties!["url"].ToString() == "http://api.internal:5000/health");
    }

    [Fact]
    public async Task Extracts_DockerDeploy()
    {
        var yaml = """
            - name: Deploy containers
              hosts: docker_hosts
              tasks:
                - name: Run app container
                  docker_container:
                    name: orders-api
                    image: registry.internal/orders-api:latest
                    state: started
            """;

        var result = await ExtractAsync(yaml, "/test/docker-deploy.yml");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["container"].ToString() == "orders-api" &&
            e.Properties!["image"].ToString() == "registry.internal/orders-api:latest");
    }

    [Fact]
    public async Task Extracts_IISDeploy()
    {
        var yaml = """
            - name: Configure IIS
              hosts: windows_servers
              tasks:
                - name: Create IIS site
                  win_iis_website:
                    name: OrdersApi
                    state: started
                    physical_path: C:\inetpub\OrdersApi
            """;

        var result = await ExtractAsync(yaml, "/test/iis-deploy.yml");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEPLOYS &&
            e.Properties!["iis_site"].ToString() == "OrdersApi");
    }

    // -- Module detection --------------------------------------------

    [Fact]
    public async Task DetectsModule_InTaskProperties()
    {
        var yaml = """
            - name: Deploy app
              hosts: all
              tasks:
                - name: Copy artifact
                  copy:
                    src: /build/app.zip
                    dest: /opt/app/app.zip
            """;

        var result = await ExtractAsync(yaml, "/test/deploy.yml");

        var task = result.Nodes.First(n =>
            n.Label == NodeLabel.AnsibleTask && n.Name == "Copy artifact");
        task.Properties["module"].ShouldBe("copy");
    }

    [Fact]
    public async Task Extracts_Metadata()
    {
        var yaml = """
            - name: Simple play
              hosts: all
              tasks:
                - name: Ping
                  ping:
            """;

        var result = await ExtractAsync(yaml, "/test/ping.yml");

        result.Metadata.ShouldNotBeNull();
        result.Metadata.Language.ShouldBe("YAML");
        result.Metadata.Framework.ShouldBe("Ansible");
    }

    // -- Static reference analysis ------------------------------------

    [Fact]
    public async Task ScansForUrls_InVariableValues()
    {
        var yaml = """
            api_url: http://orders-api.internal:5000/api
            health_endpoint: https://monitoring.corp:8443/health
            app_name: myapp
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/defaults/main.yml");

        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "ansible_url_ref" &&
            c.CalleeName.Contains("orders-api.internal"));
        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "ansible_url_ref" &&
            c.CalleeName.Contains("monitoring.corp"));
    }

    [Fact]
    public async Task ScansForHostnames_InVariableValues()
    {
        var yaml = """
            db_host: mysql.internal:3306
            redis_host: cache.corp
            simple_value: hello
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/defaults/main.yml");

        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "ansible_host_ref" &&
            c.CalleeName == "mysql.internal");
        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "ansible_host_ref" &&
            c.CalleeName == "cache.corp");
    }

    [Fact]
    public async Task ScansForConnectionStrings_InVariableValues()
    {
        var yaml = """
            connection_string: "Server=dbserver.internal;Database=orders;User=app"
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/vars/main.yml");

        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "ansible_connection_ref" &&
            c.CalleeName == "dbserver.internal");
    }

    [Fact]
    public async Task ScansForQueueNames_InVariableValues()
    {
        var yaml = """
            queue_name: order-processing-queue
            exchange_name: events-exchange
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/vars/main.yml");

        result.UnresolvedCalls.ShouldContain(c =>
            c.ReceiverType == "ansible_queue_ref");
    }

    [Fact]
    public async Task DeduplicatesReferences()
    {
        var yaml = """
            primary_url: http://api.internal:5000/v1
            backup_url: http://api.internal:5000/v2
            """;

        var result = await ExtractAsync(yaml, "/test/roles/myapp/defaults/main.yml");

        // Same host should appear only once in host refs
        var hostRefs = result.UnresolvedCalls
            .Where(c => c.ReceiverType == "ansible_host_ref" && c.CalleeName == "api.internal")
            .ToList();
        hostRefs.Count.ShouldBe(1);
    }
}
