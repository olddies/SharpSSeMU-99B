-- PostgreSQL schema for the tables JoinServer uses (account authentication). Translation of MEMB_INFO /
-- MEMB_STAT (SQL Server, see MuServer99B/DB/MuOnline.sql) to Postgres. Name mapping (the original uses SQL
-- Server-style identifiers that are not idiomatic in Postgres, e.g. "memb___id" with 3 underscores because of
-- the fixed COBOL-style field padding): memb_guid          -> memb_info.id            (SERIAL/IDENTITY)
-- memb___id          -> memb_info.account        (login) memb__pwd          -> memb_info.password       (plain
-- text, same as the original — see note) memb_name          -> memb_info.owner_name sno__numb          ->
-- memb_info.personal_code  (PersonalCode the client sees, char(18) in the original) bloc_code          ->
-- memb_info.block_code     (0 = normal account; the original uses it as char but it is read as an integer via
-- GetAsInteger, so here it is smallint) AccountLevel       -> memb_info.account_level  (VIP level)
-- AccountExpireDate  -> memb_info.account_expire (VIP level expiry) memb_stat (connection state, 1:1 with
-- memb_info by "account") ConnectStat  -> connect_stat ServerName   -> server_name IP           -> ip_address
-- ConnectTM    -> connected_at DisConnectTM -> disconnected_at SECURITY NOTE: the original stores memb__pwd in
-- plain text and compares it byte by byte (see GJConnectAccountRecv in JoinServerProtocol.cpp, MD5Encryption=0
-- branch, which is the .ini default). The same behaviour is replicated here for fidelity/compatibility -- the
-- client sends the password unencrypted inside the C1:01 packet anyway, so there is no real security downgrade
-- in keeping it like this for now. It can be migrated to a hash (e.g. bcrypt) later without touching the
-- protocol, since the comparison lives entirely on the server side.

CREATE TABLE IF NOT EXISTS memb_info (
    id              SERIAL PRIMARY KEY,
    account         VARCHAR(10) NOT NULL UNIQUE,
    password        VARCHAR(10) NOT NULL,
    owner_name      VARCHAR(10) NOT NULL DEFAULT '',
    personal_code   CHAR(18)    NOT NULL DEFAULT '0                 ',
    block_code      SMALLINT    NOT NULL DEFAULT 0,
    account_level   INTEGER     NOT NULL DEFAULT 0,
    account_expire  TIMESTAMP   NOT NULL DEFAULT '1900-01-01 00:00:00',
    email           VARCHAR(50)
);

CREATE TABLE IF NOT EXISTS memb_stat (
    account         VARCHAR(10) PRIMARY KEY REFERENCES memb_info(account),
    connect_stat    SMALLINT,
    server_name     VARCHAR(50),
    ip_address      VARCHAR(15),
    connected_at    TIMESTAMP,
    disconnected_at TIMESTAMP,
    online_seconds  INTEGER
);

-- Test accounts equivalent to the ones MuOnline.sql ships with (test/test, admin/admin).
INSERT INTO memb_info (account, password, owner_name, email)
VALUES ('test', 'test', 'SSeMU', 'test@ssemu.com'),
       ('admin', 'admin', 'SSeMU', 'admin@ssemu.com')
ON CONFLICT (account) DO NOTHING;
