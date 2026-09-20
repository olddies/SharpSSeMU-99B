mu_add_test(
    NAME test_connect_server_wire
    SOURCES
        test_connect_server_wire.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)

mu_add_test(
    NAME test_stream_cipher
    SOURCES
        test_stream_cipher.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/StreamCipher099B.cpp
)

mu_add_test(
    NAME test_block_cipher
    SOURCES
        test_block_cipher.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/BlockCipher099B.cpp
)

mu_add_test(
    NAME test_game_framer
    SOURCES
        test_game_framer.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameFramer099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/BlockCipher099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/StreamCipher099B.cpp
)

mu_add_test(
    NAME test_game_encoder
    SOURCES
        test_game_encoder.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameEncoder099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/BlockCipher099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/StreamCipher099B.cpp
)

mu_add_test(
    NAME test_game_socket
    SOURCES
        test_game_socket.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameSocket099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameFramer099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameEncoder099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/BlockCipher099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/StreamCipher099B.cpp
    LINK_LIBS
        ws2_32
)

mu_add_test(
    NAME test_login_wire
    SOURCES
        test_login_wire.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameEncoder099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/BlockCipher099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/StreamCipher099B.cpp
)

mu_add_test(
    NAME test_charset
    SOURCES
        test_charset.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/CharSet099B.cpp
)

mu_add_test(
    NAME test_move_wire
    SOURCES
        test_move_wire.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)

mu_add_test(
    NAME test_chat_combat_wire
    SOURCES
        test_chat_combat_wire.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)

mu_add_test(
    NAME test_inventory_party_wire
    SOURCES
        test_inventory_party_wire.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)

mu_add_test(
    NAME test_item_info
    SOURCES
        test_item_info.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)

mu_add_test(
    NAME test_npc_shop_wire
    SOURCES
        test_npc_shop_wire.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
        ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)
