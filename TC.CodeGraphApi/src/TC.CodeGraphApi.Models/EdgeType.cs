namespace TC.CodeGraphApi.Models;

public enum EdgeType
{
    // Containment
    CONTAINS_FILE,
    CONTAINS_FOLDER,
    CONTAINS_NAMESPACE,

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

    // Packages
    REFERENCES_PACKAGE,

    // Angular
    RENDERS,
    SUBSCRIBES,

    // Change coupling
    FILE_CHANGES_WITH,

    // Jobs
    SCHEDULES
}
