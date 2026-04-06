namespace CodeGraph.Models;

public enum EdgeType
{
    // Containment
    CONTAINS_FILE,
    CONTAINS_FOLDER,
    CONTAINS_NAMESPACE,
    CONTAINS_PROJECT,

    // Definitions
    DEFINES,
    DEFINES_METHOD,

    // References
    CALLS,
    IMPORTS,
    IMPLEMENTS,
    INHERITS,
    USES_TYPE,
    INJECTS,

    // Cross-service
    HTTP_CALLS,
    HANDLES,
    QUERIES,

    // Messaging
    PUBLISHES,
    CONSUMES,
    ROUTED_TO,
    BOUND_TO,
    REGISTERS,
    CARRIES_FIELD,

    // Packages
    REFERENCES_PACKAGE,

    // Angular
    RENDERS,
    SUBSCRIBES,

    // Change coupling
    FILE_CHANGES_WITH,

    // Jobs
    SCHEDULES,

    // Ansible / IaC
    INCLUDES_ROLE,
    NOTIFIES_HANDLER,
    DEPLOYS,
    CONFIGURES,

    // Terraform / IaC
    INCLUDES_MODULE,
    DEPENDS_ON
}
