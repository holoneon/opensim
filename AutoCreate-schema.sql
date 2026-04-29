CREATE TABLE holo_account_autocreate_log (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,

    principal_id CHAR(36) NULL,
    first_name VARCHAR(64) NOT NULL,
    last_name VARCHAR(64) NOT NULL,

    ip_address VARCHAR(45) NULL,
    viewer VARCHAR(255) NULL,

    id0_hash CHAR(64) NULL,
    mac_hash CHAR(64) NULL,
    fingerprint_hash CHAR(64) NULL,

    success TINYINT(1) NOT NULL DEFAULT 0,
    reason TEXT NULL,

    INDEX idx_created_at (created_at),
    INDEX idx_principal_id (principal_id),
    INDEX idx_name (first_name, last_name),
    INDEX idx_ip_created (ip_address, created_at),
    INDEX idx_fingerprint_created (fingerprint_hash, created_at),
    INDEX idx_id0_created (id0_hash, created_at),
    INDEX idx_mac_created (mac_hash, created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;


CREATE TABLE holo_login_fingerprints (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,

    first_seen DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
        ON UPDATE CURRENT_TIMESTAMP,

    id0_hash CHAR(64) NULL,
    mac_hash CHAR(64) NULL,
    fingerprint_hash CHAR(64) NULL,

    first_ip VARCHAR(45) NULL,
    last_ip VARCHAR(45) NULL,

    created_accounts INT UNSIGNED NOT NULL DEFAULT 0,
    failed_attempts INT UNSIGNED NOT NULL DEFAULT 0,
    blocked_until DATETIME NULL,

    UNIQUE KEY uq_fingerprint_hash (fingerprint_hash),
    INDEX idx_id0_hash (id0_hash),
    INDEX idx_mac_hash (mac_hash),
    INDEX idx_blocked_until (blocked_until),
    INDEX idx_last_seen (last_seen)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE holo_autocreate_blocks (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at DATETIME NULL,

    ip_address VARCHAR(45) NULL,
    id0_hash CHAR(64) NULL,
    mac_hash CHAR(64) NULL,
    fingerprint_hash CHAR(64) NULL,

    reason TEXT NOT NULL,

    INDEX idx_ip_address (ip_address),
    INDEX idx_id0_hash (id0_hash),
    INDEX idx_mac_hash (mac_hash),
    INDEX idx_fingerprint_hash (fingerprint_hash),
    INDEX idx_expires_at (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;


