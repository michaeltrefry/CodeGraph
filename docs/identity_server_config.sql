INSERT INTO clients (
    -- identity
    client_id, client_name, description, enabled, protocol_type,
    -- secrets & pkce
    require_client_secret, require_pkce, allow_plain_text_pkce, require_request_object,
    -- consent
    require_consent, allow_remember_consent,
    -- tokens
    always_include_user_claims_in_id_token, allow_access_tokens_via_browser,
    allow_offline_access, access_token_type, include_jwt_id,
    access_token_lifetime, identity_token_lifetime, authorization_code_lifetime,
    -- refresh tokens (not used, but NOT NULL)
    absolute_refresh_token_lifetime, sliding_refresh_token_lifetime,
    refresh_token_usage, refresh_token_expiration, update_access_token_claims_on_refresh,
    -- logout (not used, but NOT NULL)
    front_channel_logout_session_required, back_channel_logout_session_required,
    -- claims
    always_send_client_claims,
    -- login & device
    enable_local_login, device_code_lifetime, non_editable,
    -- timestamps
    created
) VALUES (
             -- identity
             'codegraph-web', 'CodeGraph Web', 'CodeGraph Angular SPA - AI Wiki and admin', 1, 'oidc',
             -- secrets & pkce
             0,          -- require_client_secret = false (public SPA client)
             1,          -- require_pkce = true
             0,          -- allow_plain_text_pkce = false (require S256)
             0,          -- require_request_object = false
             -- consent
             0,          -- require_consent = false
             1,          -- allow_remember_consent = true
             -- tokens
             0,          -- always_include_user_claims_in_id_token = false
             1,          -- allow_access_tokens_via_browser = true
             0,          -- allow_offline_access = false
             0,          -- access_token_type = 0 (JWT)
             0,          -- include_jwt_id = false
             3600,       -- access_token_lifetime = 1 hour
             300,        -- identity_token_lifetime = 5 min
             300,        -- authorization_code_lifetime = 5 min
             -- refresh tokens (defaults, not used since allow_offline_access = false)
             2592000,    -- absolute_refresh_token_lifetime = 30 days
             1296000,    -- sliding_refresh_token_lifetime = 15 days
             1,          -- refresh_token_usage = 1 (one-time)
             1,          -- refresh_token_expiration = 1 (sliding)
             0,          -- update_access_token_claims_on_refresh = false
             -- logout
             1,          -- front_channel_logout_session_required = true
             1,          -- back_channel_logout_session_required = true
             -- claims
             0,          -- always_send_client_claims = false
             -- login & device
             1,          -- enable_local_login = true
             300,        -- device_code_lifetime = 5 min
             0,          -- non_editable = false
             -- timestamps
             NOW()
         );

-- Capture the new client's id for subsequent inserts
SET @clientId = LAST_INSERT_ID();

INSERT INTO client_grant_types (client_id, grant_type) VALUES
    (@clientId, 'authorization_code');

INSERT INTO client_scopes (client_id, scope) VALUES
                                                 (@clientId, 'openid'),
                                                 (@clientId, 'username');

INSERT INTO client_redirect_uris (client_id, redirect_uri) VALUES
    (@clientId, 'http://localhost:4200/auth/callback');

INSERT INTO client_post_logout_redirect_uris (client_id, post_logout_redirect_uri) VALUES
    (@clientId, 'http://localhost:4200');

-- Staging (add when you have the URL)
INSERT INTO client_redirect_uris (client_id, redirect_uri) VALUES
    (@clientId, 'https://codegraph.stg.tcdevops.com/auth/callback');

INSERT INTO client_cors_origins (client_id, origin) VALUES
    (@clientId, 'http://localhost:4200');

-- Staging (add when you have the URL)
INSERT INTO client_cors_origins (client_id, origin) VALUES
    (@clientId, 'https://codegraph.stg.tcdevops.com');

-- Create API resource
INSERT INTO api_resources (
    enabled, name, display_name, description,
    show_in_discovery_document, created, updated, non_editable
) VALUES (
             1, 'codegraph-api', 'CodeGraph API', 'CodeGraph API resource',
             1, NOW(), NOW(), 0
         );

SET @apiResourceId = LAST_INSERT_ID();

-- Create API scope
INSERT INTO api_scopes (
    enabled, name, display_name, description,
    required, emphasized, show_in_discovery_document
) VALUES (
             1, 'codegraph-api', 'CodeGraph API', 'Access to CodeGraph API',
             0, 0, 1
         );

SET @apiScopeId = LAST_INSERT_ID();

-- Link API scope to API resource
INSERT INTO api_resource_scopes (api_resource_id, scope) VALUES
    (@apiResourceId, 'codegraph-api');

-- Add the API scope to the client
INSERT INTO client_scopes (client_id, scope) VALUES
    (@clientId, 'codegraph-api');