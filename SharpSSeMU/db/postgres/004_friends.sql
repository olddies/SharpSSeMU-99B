-- Fase 5 (GameServer): lista de amigos -- puerto simplificado de T_FriendMain/T_FriendList
-- (DataServer/Friend.cpp): el original indirecciona por un GUID numérico creado por
-- WZ_UserGuidCreate; acá se referencia directo por nombre de personaje, ya que character.name ya
-- es PK única en este puerto (no hace falta esa capa de indirección extra).
--
-- El sistema de correo entre amigos (T_FriendMail: mensajes cortos + "foto" CharSet adjunta, hasta
-- 50 por usuario, cuesta 1000 zen enviarlo) queda FUERA de esta pasada -- es una mini-feature de
-- mensajería empaquetada junto con Friend en el original, pero no una dependencia de la lista de
-- amigos en sí (documentado como deuda técnica en el README).

CREATE TABLE IF NOT EXISTS friend_list (
    owner_name  VARCHAR(10) NOT NULL REFERENCES character(name),
    friend_name VARCHAR(10) NOT NULL,
    PRIMARY KEY (owner_name, friend_name)
);

-- Solicitudes de amistad pendientes -- owner_name es a quien le llegó la invitación,
-- requester_name quien la mandó (puerto de T_WaitFriend).
CREATE TABLE IF NOT EXISTS friend_request (
    owner_name     VARCHAR(10) NOT NULL REFERENCES character(name),
    requester_name VARCHAR(10) NOT NULL,
    PRIMARY KEY (owner_name, requester_name)
);
