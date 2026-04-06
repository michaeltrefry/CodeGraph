namespace TC.CodeGraphApi.Extractors.Ansible;

internal enum AnsibleFileType
{
    NotAnsible,
    Playbook,
    TasksFile,
    HandlersFile,
    VarsFile,
    DefaultsFile,
    MetaFile,
    RequirementsFile
}
