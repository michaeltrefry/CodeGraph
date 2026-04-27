ALTER TABLE repositories
    ADD COLUMN IF NOT EXISTS dotnet_support JSON NULL;
