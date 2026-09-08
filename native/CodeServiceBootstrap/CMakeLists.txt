cmake_minimum_required(VERSION 3.25)

project(CodeServiceBootstrap LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "CodeServiceBootstrap is Windows-only.")
endif()

if(NOT MSVC)
    message(FATAL_ERROR "CodeServiceBootstrap currently requires the MSVC toolchain.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "CodeServiceBootstrap is x86_64-only.")
endif()

add_library(CodeServiceBootstrap SHARED
    src/CodeServiceBootstrap.cpp
)

target_compile_features(CodeServiceBootstrap PRIVATE cxx_std_17)

target_compile_options(CodeServiceBootstrap PRIVATE
    /O2
    /GR-
    /W4
    /permissive-
)

target_link_libraries(CodeServiceBootstrap PRIVATE
    Shell32
    Ole32
)

set_property(TARGET CodeServiceBootstrap PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded"
)

set_target_properties(CodeServiceBootstrap PROPERTIES
    PREFIX ""
    OUTPUT_NAME "CodeServiceBootstrap.windows.editor.x86_64"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/lib"
    PDB_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/pdb"
)
