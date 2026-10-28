cmake_minimum_required(VERSION 3.20)
project(GetDocumentEuroOfficeHeaderProbe LANGUAGES CXX)

# Explicit, pre-extracted, pinned upstream sources. NEVER auto-clone/download.
if(NOT DEFINED EUROOFFICE_DESKTOP_TREE OR NOT IS_DIRECTORY "${EUROOFFICE_DESKTOP_TREE}")
  message(FATAL_ERROR "Set -DEUROOFFICE_DESKTOP_TREE=/path/to/pinned/DesktopEditors")
endif()
set(UPSTREAM "${EUROOFFICE_DESKTOP_TREE}")
set(SDK "${UPSTREAM}/desktop-sdk/ChromiumBasedEditors/lib")
set(API "${SDK}/include/applicationmanager.h")
set(WIDGET "${SDK}/qt_wrapper/include/qcefview.h")
if(NOT EXISTS "${API}" OR NOT EXISTS "${WIDGET}")
  message(FATAL_ERROR "Pinned DesktopEditors SDK headers missing. Run git submodule update OFFLINE from an approved mirror.")
endif()
# Fail closed unless the exact reviewed upstream commits are present.
# This probe does not use FetchContent, ExternalProject or network requests.
find_package(Git REQUIRED)
execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${UPSTREAM}" rev-parse HEAD
    RESULT_VARIABLE upstream_code OUTPUT_VARIABLE upstream_sha
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET
)
if(NOT upstream_code EQUAL 0 OR
   NOT upstream_sha STREQUAL "6a671bb1ad238c61965a64de3a005c420940c814")
  message(FATAL_ERROR "DesktopEditors checkout is not at the pinned GD-31J revision")
endif()
execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${UPSTREAM}/desktop-sdk" rev-parse HEAD
    RESULT_VARIABLE sdk_code OUTPUT_VARIABLE sdk_sha
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET
)
if(NOT sdk_code EQUAL 0 OR
   NOT sdk_sha STREQUAL "8cc8d747cb48953bf8020c2bfbb67ede6cb55c51")
  message(FATAL_ERROR "desktop-sdk is missing or has an unreviewed revision")
endif()
execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${UPSTREAM}/core" rev-parse HEAD
    RESULT_VARIABLE core_code OUTPUT_VARIABLE core_sha
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET
)
if(NOT core_code EQUAL 0 OR
   NOT core_sha STREQUAL "a4a1ba1e9c8e6b6c841c3f059b3a8f82177d63f5")
  message(FATAL_ERROR "core submodule is missing or has an unreviewed revision")
endif()

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets OpenGLWidgets)

add_library(getdoc_gd31j_header_probe OBJECT sdk_api_probe.cpp)
target_compile_features(getdoc_gd31j_header_probe PRIVATE cxx_std_17)
target_include_directories(getdoc_gd31j_header_probe PRIVATE
    "${SDK}/include"
    "${SDK}/qt_wrapper/include"
)
target_link_libraries(getdoc_gd31j_header_probe PRIVATE
    Qt6::Core Qt6::Gui Qt6::Widgets Qt6::OpenGLWidgets
)
if(WIN32)
  target_compile_definitions(getdoc_gd31j_header_probe PRIVATE WIN32 _WINDOWS NOMINMAX)
elseif(UNIX AND NOT APPLE)
  target_compile_definitions(getdoc_gd31j_header_probe PRIVATE _LINUX LINUX OS_LINUX _X11)
endif()
message(STATUS "GD-31J compiles only upstream header/API signatures; no application/CEF process will run.")
