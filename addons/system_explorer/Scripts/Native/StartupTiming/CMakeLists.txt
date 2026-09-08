cmake_minimum_required(VERSION 3.25)

project(SystemExplorerStartupTiming LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "SystemExplorer StartupTiming v1 is a Windows-only diagnostic spike.")
endif()

if(NOT MSVC)
    message(FATAL_ERROR "SystemExplorer StartupTiming v1 currently requires the MSVC toolchain.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "SystemExplorer StartupTiming v1 is x86_64-only.")
endif()

add_library(system_explorer_startup_timing SHARED
    src/SystemExplorerStartupTiming.cpp
)

target_compile_features(system_explorer_startup_timing PRIVATE cxx_std_17)

target_compile_options(system_explorer_startup_timing PRIVATE
    /O2
    /GR-
    /W4
    /permissive-
)

set_property(TARGET system_explorer_startup_timing PROPERTY
    MSVC_RUNTIME_LIBRARY "MultiThreaded"
)

set_target_properties(system_explorer_startup_timing PROPERTIES
    PREFIX ""
    OUTPUT_NAME "system_explorer_startup_timing.windows.editor.x86_64"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_SOURCE_DIR}/bin"
    ARCHIVE_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/lib"
    PDB_OUTPUT_DIRECTORY_RELEASE "${CMAKE_CURRENT_BINARY_DIR}/pdb"
)
