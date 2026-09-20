-- Esquema PostgreSQL para las tablas que usa JoinServer (autenticación de cuentas).
-- Traducción de MEMB_INFO / MEMB_STAT (SQL Server, ver MuServer99B/DB/MuOnline.sql) a Postgres.
--
-- Mapeo de nombres (el original usa identificadores estilo SQL Server que no son idiomáticos
-- en Postgres, ej. "memb___id" con 3 guiones bajos por el padding fijo de campos COBOL-style):
--   memb_guid          -> memb_info.id            (SERIAL/IDENTITY)
--   memb___id          -> memb_info.account        (login)
--   memb__pwd          -> memb_info.password       (texto plano, igual que el original — ver nota)
--   memb_name          -> memb_info.owner_name
--   sno__numb          -> memb_info.personal_code  (PersonalCode que ve el cliente, char(18) en original)
--   bloc_code          -> memb_info.block_code     (0 = cuenta normal; el original lo usa como char pero se
--                                                    lee como entero vía GetAsInteger, así que aquí es smallint)
--   AccountLevel       -> memb_info.account_level  (nivel VIP)
--   AccountExpireDate  -> memb_info.account_expire (vencimiento del nivel VIP)
--
--   memb_stat (estado de conexión, 1:1 con memb_info por "account")
--     ConnectStat  -> connect_stat
--     ServerName   -> server_name
--     IP           -> ip_address
--     ConnectTM    -> connected_at
--     DisConnectTM -> disconnected_at
--
-- NOTA DE SEGURIDAD: el original guarda memb__pwd en texto plano y lo compara byte a byte
-- (ver GJConnectAccountRecv en JoinServerProtocol.cpp, rama MD5Encryption=0, que es el default
-- del .ini). Se replica ese mismo comportamiento aquí por fidelidad/compatibilidad -- el cliente
-- de todos modos manda la contraseña sin cifrar dentro del paquete C1:01, así que no hay downgrade
-- de seguridad real al mantenerlo así por ahora. Se puede migrar a hash (ej. bcrypt) más adelante
-- sin tocar el protocolo, ya que la comparación vive enteramente del lado del servidor.

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

-- Cuentas de prueba equivalentes a las que trae MuOnline.sql de fábrica (test/test, admin/admin).
INSERT INTO memb_info (account, password, owner_name, email)
VALUES ('test', 'test', 'SSeMU', 'test@ssemu.com'),
       ('admin', 'admin', 'SSeMU', 'admin@ssemu.com')
ON CONFLICT (account) DO NOTHING;
