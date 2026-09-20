-- PostgreSQL schema for the tables DataServer uses (characters, inventory, rankings, etc). Translation of the
-- corresponding tables of MuServer99B/DB/MuOnline.sql (SQL Server) to Postgres. The binary blobs
-- (inventory/skills/quest/effects) are stored as they are as bytea — same byte layout the client/GameServer
-- uses, so their internal format does not have to be touched.

CREATE TABLE IF NOT EXISTS account_character (
    id           SERIAL PRIMARY KEY,
    account      VARCHAR(10) NOT NULL UNIQUE REFERENCES memb_info(account),
    game_id1     VARCHAR(10),
    game_id2     VARCHAR(10),
    game_id3     VARCHAR(10),
    game_id4     VARCHAR(10),
    game_id5     VARCHAR(10),
    game_idc     VARCHAR(10), -- último personaje usado (para reconectar directo)
    move_cnt     SMALLINT NOT NULL DEFAULT 0,
    ext_class    INTEGER NOT NULL DEFAULT 2,
    locked       INTEGER NOT NULL DEFAULT 0
);

-- Plantillas de personaje nuevo por clase (usadas al crear personaje). Datos reales importados
-- de MuOnline.sql en 003_default_class_seed.sql (5 clases: 0=DW, 16=DK, 32=FE, 48=MG, 64=SUM).
CREATE TABLE IF NOT EXISTS default_class_type (
    class          SMALLINT PRIMARY KEY,
    level          SMALLINT NOT NULL DEFAULT 1,
    level_up_point SMALLINT NOT NULL DEFAULT 0,
    strength       SMALLINT NOT NULL DEFAULT 0,
    dexterity      SMALLINT NOT NULL DEFAULT 0,
    vitality       SMALLINT NOT NULL DEFAULT 0,
    energy         SMALLINT NOT NULL DEFAULT 0,
    leadership     SMALLINT NOT NULL DEFAULT 0,
    inventory      BYTEA,   -- 1728 bytes (INVENTORY_SIZE=108 * 16)
    magic_list     BYTEA,   -- 180 bytes (MAX_SKILL_LIST=60 * 3)
    life           REAL,
    max_life       REAL,
    mana           REAL,
    max_mana       REAL,
    map_number     SMALLINT,
    map_pos_x      SMALLINT,
    map_pos_y      SMALLINT,
    quest          BYTEA,   -- 50 bytes
    db_version     SMALLINT,
    effect_list    BYTEA    -- 208 bytes (MAX_EFFECT_LIST=16 * 13)
);

CREATE TABLE IF NOT EXISTS character (
    account_id        VARCHAR(10) NOT NULL REFERENCES memb_info(account),
    name              VARCHAR(10) PRIMARY KEY,
    clevel            INTEGER NOT NULL DEFAULT 1,
    level_up_point    INTEGER NOT NULL DEFAULT 0,
    class             SMALLINT NOT NULL,
    experience        BIGINT NOT NULL DEFAULT 0,
    strength          INTEGER NOT NULL DEFAULT 0,
    dexterity         INTEGER NOT NULL DEFAULT 0,
    vitality          INTEGER NOT NULL DEFAULT 0,
    energy            INTEGER NOT NULL DEFAULT 0,
    leadership        INTEGER NOT NULL DEFAULT 0,
    inventory         BYTEA,
    magic_list        BYTEA,
    money             BIGINT NOT NULL DEFAULT 0,
    life              REAL NOT NULL DEFAULT 0,
    max_life          REAL NOT NULL DEFAULT 0,
    mana              REAL NOT NULL DEFAULT 0,
    max_mana          REAL NOT NULL DEFAULT 0,
    bp                REAL NOT NULL DEFAULT 0,
    max_bp            REAL NOT NULL DEFAULT 0,
    map_number        SMALLINT NOT NULL DEFAULT 0,
    map_pos_x         SMALLINT NOT NULL DEFAULT 0,
    map_pos_y         SMALLINT NOT NULL DEFAULT 0,
    map_dir           SMALLINT NOT NULL DEFAULT 0,
    pk_count          INTEGER NOT NULL DEFAULT 0,
    pk_level          INTEGER NOT NULL DEFAULT 3,
    pk_time           INTEGER NOT NULL DEFAULT 0,
    mdate             TIMESTAMP NOT NULL DEFAULT now(),
    ldate             TIMESTAMP NOT NULL DEFAULT now(),
    ctl_code          SMALLINT NOT NULL DEFAULT 0,
    db_version        SMALLINT NOT NULL DEFAULT 0,
    quest             BYTEA,
    chat_limit_time   INTEGER NOT NULL DEFAULT 0,
    fruit_point       INTEGER NOT NULL DEFAULT 0,
    effect_list       BYTEA,
    fruit_add_point   INTEGER NOT NULL DEFAULT 0,
    fruit_sub_point   INTEGER NOT NULL DEFAULT 0,
    reset_count       INTEGER NOT NULL DEFAULT 0,
    master_reset_count INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS option_data (
    name         VARCHAR(10) PRIMARY KEY REFERENCES character(name),
    skill_key    BYTEA, -- 10 bytes
    game_option  SMALLINT,
    qkey         SMALLINT,
    wkey         SMALLINT,
    ekey         SMALLINT,
    chat_window  SMALLINT
);

CREATE TABLE IF NOT EXISTS reset_data (
    account              VARCHAR(10) NOT NULL,
    name                 VARCHAR(10) NOT NULL,
    reset_day            INTEGER NOT NULL DEFAULT 0,
    reset_wek            INTEGER NOT NULL DEFAULT 0,
    reset_mon            INTEGER NOT NULL DEFAULT 0,
    reset_date_day       TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    reset_date_wek       TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    reset_date_mon       TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    master_reset_day     INTEGER NOT NULL DEFAULT 0,
    master_reset_wek     INTEGER NOT NULL DEFAULT 0,
    master_reset_mon     INTEGER NOT NULL DEFAULT 0,
    master_reset_date_day TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    master_reset_date_wek TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    master_reset_date_mon TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    PRIMARY KEY (account, name)
);

CREATE TABLE IF NOT EXISTS event_entry_count (
    account   VARCHAR(10) NOT NULL,
    name      VARCHAR(10) NOT NULL,
    bc_count  INTEGER NOT NULL DEFAULT 0,
    cc_count  INTEGER NOT NULL DEFAULT 0,
    ds_count  INTEGER NOT NULL DEFAULT 0,
    last_date TIMESTAMP NOT NULL DEFAULT '1900-01-01',
    PRIMARY KEY (account, name)
);

CREATE TABLE IF NOT EXISTS ranking_duel (
    name       VARCHAR(10) PRIMARY KEY,
    win_score  INTEGER NOT NULL DEFAULT 0,
    lose_score INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS ranking_blood_castle (
    name  VARCHAR(10) PRIMARY KEY,
    score INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS ranking_chaos_castle (
    name  VARCHAR(10) PRIMARY KEY,
    score INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS ranking_devil_square (
    name  VARCHAR(10) PRIMARY KEY,
    score INTEGER NOT NULL DEFAULT 0
);

-- It did not exist in the original MuOnline.sql (the DataServer binary references it but the SQL Server dump
-- does not include it — probably a feature added later without updating the script). It is added anyway so that
-- the function is actually operational in this port.
CREATE TABLE IF NOT EXISTS ranking_illusion_temple (
    name  VARCHAR(10) PRIMARY KEY,
    score INTEGER NOT NULL DEFAULT 0
);

-- It did not exist in the original MuOnline.sql either, same case as ranking_illusion_temple.
CREATE TABLE IF NOT EXISTS monster_kill_count (
    name           VARCHAR(10) NOT NULL,
    monster_class  INTEGER NOT NULL,
    kill_count     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (name, monster_class)
);

CREATE TABLE IF NOT EXISTS pet_item_info (
    item_serial BIGINT PRIMARY KEY,
    pet_level   SMALLINT NOT NULL DEFAULT 1,
    pet_exp     BIGINT NOT NULL DEFAULT 0
);

-- Single row (number=0) used as the global counter of item serials (WZ_GetItemSerial).
CREATE TABLE IF NOT EXISTS game_server_info (
    number         INTEGER PRIMARY KEY DEFAULT 0,
    item_count     BIGINT NOT NULL DEFAULT 0,
    zen_count      BIGINT NOT NULL DEFAULT 0,
    ace_item_count BIGINT NOT NULL DEFAULT 0
);

INSERT INTO game_server_info (number, item_count, zen_count, ace_item_count)
VALUES (0, 0, 0, 0)
ON CONFLICT (number) DO NOTHING;
