namespace CodeGraph.Models;

public enum NodeLabel
{
    // Structural
    Repository,
    DotnetProject,
    Namespace,
    Folder,
    File,

    // Code elements
    Class,
    Interface,
    Enum,
    Struct,
    Record,
    Function,
    Method,
    Property,
    Constructor,
    Delegate,

    // Infrastructure
    Route,
    Service,
    Table,
    View,
    StoredProcedure,

    // Messaging
    Event,
    Queue,
    Exchange,

    // Angular
    Component,
    Module,

    // Jobs
    Job,

    // Packages
    NuGetPackage,
    Package,

    // Semantic targets defined outside the indexed repository
    ExternalSymbol,

    // Ansible / IaC
    Playbook,
    Role,
    AnsibleTask,
    AnsibleHandler,
    AnsibleVariable,

    // Terraform / IaC
    TerraformResource,
    TerraformModule,
    TerraformVariable,
    TerraformOutput,
    TerraformDataSource,

    // Database schema
    Database,
    Column
}
