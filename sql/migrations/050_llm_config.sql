CREATE TABLE IF NOT EXISTS llm_config (
    config_key VARCHAR(255) NOT NULL PRIMARY KEY,
    config_value LONGTEXT NULL,
    updated_by VARCHAR(255) NULL,
    updated_at_utc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6)
);

CREATE TABLE IF NOT EXISTS llm_provider_models (
    provider_key VARCHAR(64) NOT NULL,
    model_id VARCHAR(255) NOT NULL,
    display_order INT NOT NULL DEFAULT 0,
    PRIMARY KEY (provider_key, model_id),
    INDEX ix_llm_provider_models_provider_order (provider_key, display_order)
);
