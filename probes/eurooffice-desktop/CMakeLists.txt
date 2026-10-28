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
