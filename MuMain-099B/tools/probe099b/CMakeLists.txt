# Sonda manual contra un GameServer vivo. No se registra en CTest a propósito:
# necesita un servidor corriendo, y los tests tienen que poder correr solos.
add_executable(probe099b
    main.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameSocket099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameFramer099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/GameEncoder099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/BlockCipher099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/StreamCipher099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/Wire099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/CharSet099B.cpp
    ${CMAKE_SOURCE_DIR}/src/source/Protocol099B/ItemInfo099B.cpp
)
target_include_directories(probe099b PRIVATE ${CMAKE_SOURCE_DIR}/src/source)
target_compile_features(probe099b PRIVATE cxx_std_20)
target_link_libraries(probe099b PRIVATE ws2_32)
if(MSVC)
    target_compile_options(probe099b PRIVATE /utf-8)
endif()
