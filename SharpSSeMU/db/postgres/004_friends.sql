-- Phase 5 (GameServer): friend list -- simplified port of T_FriendMain/T_FriendList (DataServer/Friend.cpp):
-- the original indirects through a numeric GUID created by WZ_UserGuidCreate; here it is referenced directly by
-- character name, since character.name is already a unique PK in this port (that extra layer of indirection is
-- not needed). The mail system between friends (T_FriendMail: short messages + attached CharSet "photo", up to
-- 50 per user, costs 1000 zen to send) is OUT of this pass -- it is a mini messaging feature packaged together
-- with Friend in the original, but not a dependency of the friend list itself (documented as technical debt in
-- the README).

CREATE TABLE IF NOT EXISTS friend_list (
    owner_name  VARCHAR(10) NOT NULL REFERENCES character(name),
    friend_name VARCHAR(10) NOT NULL,
    PRIMARY KEY (owner_name, friend_name)
);

-- Pending friend requests -- owner_name is who the invitation reached, requester_name who sent it (port of
-- T_WaitFriend).
CREATE TABLE IF NOT EXISTS friend_request (
    owner_name     VARCHAR(10) NOT NULL REFERENCES character(name),
    requester_name VARCHAR(10) NOT NULL,
    PRIMARY KEY (owner_name, requester_name)
);
