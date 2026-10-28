cmake_minimum_required(VERSION 3.21)
project(GetDocumentCefInitializationLab LANGUAGES CXX)

# A strictly opt-in LAB program. Does not fetch/build upstream SDK libraries.
option(GETDOC_GD31J3_ENABLE_CEF_LAB "Build the isolated CEF initialization probe" OFF)
if(NOT GETDOC_GD31J3_ENABLE_CEF_LAB)
  message(STATUS "GD-31J.3 CEF lab is DISABLED. No binaries or SDK dependencies.")
  return()
endif()

if(NOT GETDOC_GD31J3_LOCAL_ISOLATION_CONFIRMED)
  message(FATAL_ERROR "GD-31J.3 needs a dedicated offline test machine; CEF may be unsandboxed.")
endif()
if(NOT DEFINED EUROOFFICE_DESKTOP_TREE OR
   NOT IS_DIRECTORY "${EUROOFFICE_DESKTOP_TREE}")
  message(FATAL_ERROR "Provide a previously approved pinned local DesktopEditors source tree.")
endif()
if(NOT DEFINED EUROOFFICE_RUNTIME_LIB_DIR OR
   NOT IS_DIRECTORY "${EUROOFFICE_RUNTIME_LIB_DIR}")
  message(FATAL_ERROR "Provide a directory with pre-built approved local SDK libraries.")
endif()
set(_sdk "${EUROOFFICE_DESKTOP_TREE}/desktop-sdk/ChromiumBasedEditors/lib")
set(_qt_include "${_sdk}/qt_wrapper/include")
if(NOT EXISTS "${_sdk}/include/cefapplication.h" OR
   NOT EXISTS "${_qt_include}/qascapplicationmanager.h")
  message(FATAL_ERROR "Pinned local EuroOffice SDK API headers missing.")
endif()

find_package(Git REQUIRED)
foreach(_entry
    "DesktopEditors|${EUROOFFICE_DESKTOP_TREE}|6a671bb1ad238c61965a64de3a005c420940c814"
    "desktop-sdk|${EUROOFFICE_DESKTOP_TREE}/desktop-sdk|8cc8d747cb48953bf8020c2bfbb67ede6cb55c51"
    "core|${EUROOFFICE_DESKTOP_TREE}/core|a4a1ba1e9c8e6b6c841c3f059b3a8f82177d63f5")
  string(REPLACE "|" ";" _parts "${_entry}")
  list(GET _parts 0 _name)
  list(GET _parts 1 _dir)
  list(GET _parts 2 _expect)
  execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${_dir}" rev-parse HEAD
    RESULT_VARIABLE _rc OUTPUT_VARIABLE _got
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
  if(NOT _rc EQUAL 0 OR NOT _got STREQUAL _expect)
    message(FATAL_ERROR "Unreviewed ${_name} revision. GD-31J.3 refuses to configure.")
  endif()
  execute_process(
    COMMAND "${GIT_EXECUTABLE}" -C "${_dir}" status --porcelain --untracked-files=no
    RESULT_VARIABLE _status_rc OUTPUT_VARIABLE _dirty
    OUTPUT_STRIP_TRAILING_WHITESPACE ERROR_QUIET)
  if(NOT _status_rc EQUAL 0 OR NOT "${_dirty}" STREQUAL "")
    message(FATAL_ERROR "${_name} has modified tracked files. Refusing CEF lab.")
  endif()
endforeach()

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets OpenGLWidgets)
# No add_subdirectory(vendor), FetchContent, downloads, online NuGet or CI.
# The operator must provide matching locally built, verified SDK binaries.
find_library(GETDOC_ASC_DOC_SCORE NAMES ascdocumentscore
    PATHS "${EUROOFFICE_RUNTIME_LIB_DIR}" NO_DEFAULT_PATH REQUIRED)
find_library(GETDOC_ASC_QT_SCORE NAMES qtascdocumentscore
    PATHS "${EUROOFFICE_RUNTIME_LIB_DIR}" NO_DEFAULT_PATH REQUIRED)

add_executable(getdoc-eurooffice-cef-init-lab cef_init_smoke.cpp)
target_compile_features(getdoc-eurooffice-cef-init-lab PRIVATE cxx_std_17)
target_include_directories(getdoc-eurooffice-cef-init-lab PRIVATE
    "${_sdk}/include"
    "${_qt_include}"
)
target_link_libraries(getdoc-eurooffice-cef-init-lab PRIVATE
    Qt6::Core Qt6::Gui Qt6::Widgets Qt6::OpenGLWidgets
    "${GETDOC_ASC_DOC_SCORE}" "${GETDOC_ASC_QT_SCORE}"
)
# Do NOT add this target to GET's desktop project/installer.
message(WARNING "CEF init smoke ONLY: no files/editor/save/print, no security attestation.")
