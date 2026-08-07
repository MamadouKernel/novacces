-- =============================================================================
-- SIGASACCÈS / NOVACCES — SCRIPT SQL DE PROVISIONNEMENT PRODUCTION
-- Séparation stricte des rôles DDL / Runtime & Partitionnement de scan_logs
-- =============================================================================

-- 1. CRÉATION DES RÔLES POSTGRESQL (ÉVICTION DES DROITS DDL EN RUNTIME)
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'novacces_owner') THEN
        CREATE ROLE novacces_owner WITH LOGIN PASSWORD 'ChangerPasswordOwnerEnProd_2026!';
    END IF;
    IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'novacces_app') THEN
        CREATE ROLE novacces_app WITH LOGIN PASSWORD 'ChangerPasswordAppEnProd_2026!';
    END IF;
END $$;

-- 2. PARTITIONNEMENT DE LA TABLE DES JOURNAUX (scan_logs par année)
-- Préparation du schéma public ou des schémas de site (ex. site_plateau)
CREATE TABLE IF NOT EXISTS public.scan_logs_2026 PARTITION OF public.scan_logs
    FOR VALUES FROM ('2026-01-01 00:00:00+00') TO ('2027-01-01 00:00:00+00');

CREATE TABLE IF NOT EXISTS public.scan_logs_2027 PARTITION OF public.scan_logs
    FOR VALUES FROM ('2027-01-01 00:00:00+00') TO ('2028-01-01 00:00:00+00');

CREATE TABLE IF NOT EXISTS public.scan_logs_2028 PARTITION OF public.scan_logs
    FOR VALUES FROM ('2028-01-01 00:00:00+00') TO ('2029-01-01 00:00:00+00');

-- 3. RESTRICTION STRICTE DES PRIVILÈGES SUR RÔLE RUNTIME (novacces_app)
-- Permet la lecture/écriture courante, interdit DELETE sur les tables d'audit
GRANT USAGE ON SCHEMA public TO novacces_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA public TO novacces_app;
REVOKE DELETE ON TABLE public.scan_logs FROM novacces_app;
REVOKE DELETE ON TABLE public.admin_audit FROM novacces_app;
