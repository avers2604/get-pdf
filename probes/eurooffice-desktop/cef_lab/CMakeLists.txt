cmake_minimum_required(VERSION 3.21)
project(GetDocumentCefInitializationLab LANGUAGES CXX)

# A strictly opt-in LAB program. Does not fetch/build upstream SDK libraries.
option(GETDOC_GD31J3_ENABLE_CEF_LAB "Build the isolated CEF initialization probe" OFF)
option(GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB "Build one empty QCefView experiment; never open documents" OFF)
# Synthetic fixtures ONLY. Requires GD-31J.4 plus an isolated offline lab.
option(GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB "Build the synthetic-only Office file opening experiment" OFF)
# Capture actual CEF OnPaint for an approved synthetic-only Wayland lab.
# Windows/X11 CEF uses native child windows in this upstream SDK; no OSR there.
option(GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB "Enable real Wayland CEF OnPaint lab snapshot (never production)" OFF)
# Streaming is a second opt-in: no change to the earlier one-frame lab mode.
option(GETDOC_GD31J13_ENABLE_OSR_STREAM_LAB "Enable session-bound CEF OSR preview streaming on isolated Wayland VM" OFF)
# Separate opt-in for input on the bounded synthetic-only stream.
option(GETDOC_GD31J14_ENABLE_INPUT_LAB "Enable Wayland pointer/wheel/focus-only input experiment" OFF)
# Additional explicit privilege for left click, double click and selection drag.
option(GETDOC_GD31J15_ENABLE_CLICK_LAB "Enable synthetic-only Wayland CEF left-click and selection drag experiment" OFF)
# Deliberately separate from mouse privileges: nav keys and single committed
# BMP text char; full IME composition and clipboard remain prohibited.
option(GETDOC_GD31J16_ENABLE_KEYBOARD_LAB "Enable synthetic-only Wayland CEF navigation and committed character experiment" OFF)
# IME preedit never leaves the GET shell. Only explicitly committed BMP
# strings are encoded as bounded numeric codepoints in the synthetic lab.
option(GETDOC_GD31J17_ENABLE_IME_COMMIT_LAB "Enable synthetic Wayland final IME commit experiment without clipboard" OFF)
# Separate Linux/Wayland opt-in: observe pinned SDK GetLocalFilePath only.
# No document import guarantee, editing, save, clipboard or launch privileges.
option(GETDOC_GD31J22_ENABLE_BINDING_LAB "Observe synthetic editor local-file binding via pinned SDK, no production rights" OFF)
# Separate synthetic-only artifact candidate gate, NOT a document ready signal.
option(GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB "Inspect isolated Editor.bin candidate, no edit/save or OOXML readiness" OFF)
# This is a SEPARATE manually overlaid SDK lab, NEVER the normal editor build.
option(GETDOC_GD31J24_ENABLE_READY_EVENT_LAB "Observe untrusted renderer content-ready notification only" OFF)
# Independently gated native save experiment, working copy in QTemporaryDir
# only. Output is an UNTRUSTED quarantine candidate, never user/WORM file.
option(GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB "Native save only private synthetic working copy" OFF)
# Additional opt-in CEF Save As transaction, not a general-purpose file picker.
option(GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB "Use SDK Save As dialog only private same-format Save As path" OFF)
if(GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB AND NOT GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB)
  message(FATAL_ERROR "GD-31J.26 requires pinned GD-31J.25 save-copy laboratory.")
endif()
if(GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB AND NOT GETDOC_GD31J24_ENABLE_READY_EVENT_LAB)
  message(FATAL_ERROR "GD-31J.25 requires approved native content-ready lab.")
endif()
if(GETDOC_GD31J24_ENABLE_READY_EVENT_LAB AND NOT GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB)
  message(FATAL_ERROR "GD-31J.24 requires GD-31J.23 private candidate lab.")
endif()

if(GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB AND NOT GETDOC_GD31J22_ENABLE_BINDING_LAB)
  message(FATAL_ERROR "GD-31J.23 requires independently approved GD-31J.22 binding lab.")
endif()

if(GETDOC_GD31J22_ENABLE_BINDING_LAB AND NOT GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB)
  message(FATAL_ERROR "GD-31J.22 needs the approved synthetic GD-31J.6 file-open lab.")
endif()
if(GETDOC_GD31J22_ENABLE_BINDING_LAB AND NOT (UNIX AND NOT APPLE))
  message(FATAL_ERROR "GD-31J.22 synthetic binding telemetry is Linux/Wayland-only.")
endif()
if(GETDOC_GD31J17_ENABLE_IME_COMMIT_LAB AND NOT GETDOC_GD31J16_ENABLE_KEYBOARD_LAB)
  message(FATAL_ERROR "GD-31J.17 needs the approved GD-31J.16 keyboard laboratory.")
endif()
if(GETDOC_GD31J16_ENABLE_KEYBOARD_LAB AND NOT GETDOC_GD31J15_ENABLE_CLICK_LAB)
  message(FATAL_ERROR "GD-31J.16 requires explicitly enabled GD-31J.15 click lab.")
endif()
if(GETDOC_GD31J15_ENABLE_CLICK_LAB AND NOT GETDOC_GD31J14_ENABLE_INPUT_LAB)
  message(FATAL_ERROR "GD-31J.15 requires GD-31J.14 input lab with explicit approval.")
endif()
if(GETDOC_GD31J14_ENABLE_INPUT_LAB AND NOT GETDOC_GD31J13_ENABLE_OSR_STREAM_LAB)
  message(FATAL_ERROR "GD-31J.14 requires explicitly enabled synthetic CEF OSR streaming.")
endif()
if(GETDOC_GD31J13_ENABLE_OSR_STREAM_LAB AND NOT GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB)
  message(FATAL_ERROR "GD-31J.13 requires explicit GD-31J.12 Wayland CEF OSR lab.")
endif()
if(GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB AND NOT GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB)
  message(FATAL_ERROR "GD-31J.12 needs the explicit synthetic-only GD-31J.6 lab.")
endif()
if(GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB AND NOT (UNIX AND NOT APPLE))
  message(FATAL_ERROR "This pinned EuroOffice CEF OSR implementation is Wayland/Linux-only.")
endif()
if(GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB AND NOT GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB)
  message(FATAL_ERROR "GD-31J.6 requires opt-in GD-31J.4 empty editor view and its isolation gates.")
endif()
if(GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB AND NOT GETDOC_GD31J3_ENABLE_CEF_LAB)
  message(FATAL_ERROR "GD-31J.4 requires explicitly enabling the isolated GD-31J.3 CEF lab.")
endif()
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
  if(GETDOC_GD31J24_ENABLE_READY_EVENT_LAB AND _name STREQUAL "desktop-sdk")
    # This exception is permitted ONLY for the exact reviewed two-file overlay
    # in a separately acknowledged, disposable SDK checkout. No blanket dirty.
    find_package(Python3 REQUIRED COMPONENTS Interpreter)
    execute_process(
      COMMAND "${Python3_EXECUTABLE}" -I
        "${CMAKE_CURRENT_SOURCE_DIR}/ready_bridge_overlay.py" --verify "${_dir}"
      RESULT_VARIABLE _bridge_rc OUTPUT_QUIET ERROR_VARIABLE _bridge_error)
    if(NOT _status_rc EQUAL 0 OR NOT _bridge_rc EQUAL 0)
      message(FATAL_ERROR "GD-31J.24 patched SDK failed exact overlay verification: ${_bridge_error}")
    endif()
  elseif(NOT _status_rc EQUAL 0 OR NOT "${_dirty}" STREQUAL "")
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
if(GETDOC_GD31J24_ENABLE_READY_EVENT_LAB)
  # Ensure operators deliberately pin BOTH native runtime libraries rebuilt
  # for the approved lab fork. A file hash is NOT an attestation of build
  # provenance: a separate manual runtime linkage review is still required.
  foreach(_pin_name ASC_DOCUMENTSCORE ASC_QT_SCORE)
    set(_pin_var "GETDOC_GD31J24_APPROVED_${_pin_name}_SHA256")
    if(NOT DEFINED ${_pin_var})
      message(FATAL_ERROR "GD-31J.24 requires approved exact SHA-256 in ${_pin_var}")
    endif()
    string(LENGTH "${${_pin_var}}" _approved_hash_length)
    if(NOT _approved_hash_length EQUAL 64 OR NOT "${${_pin_var}}" MATCHES "^[0-9a-fA-F]+$")
      message(FATAL_ERROR "GD-31J.24 expected a 64-character hex SHA-256 in ${_pin_var}")
    endif()
    if(_pin_name STREQUAL "ASC_DOCUMENTSCORE")
      set(_lib "${GETDOC_ASC_DOC_SCORE}")
    else()
      set(_lib "${GETDOC_ASC_QT_SCORE}")
    endif()
    file(SHA256 "${_lib}" _actual_sdk_hash)
    string(TOLOWER "${_actual_sdk_hash}" _actual_sdk_hash)
    string(TOLOWER "${${_pin_var}}" _expected_sdk_hash)
    if(NOT _actual_sdk_hash STREQUAL _expected_sdk_hash)
      message(FATAL_ERROR "GD-31J.24 runtime SHA does not match approved pin: ${_pin_name}")
    endif()
  endforeach()
endif()

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

# GD-31J.4 is a SECOND, even higher-risk, opt-in executable. The vendor
# CEF runtime may be unsandboxed. This is not added to the GET installer.
if(GETDOC_GD31J4_ENABLE_EMPTY_VIEW_LAB)
  add_executable(getdoc-eurooffice-cef-empty-view-lab cef_empty_view.cpp)
  target_compile_features(getdoc-eurooffice-cef-empty-view-lab PRIVATE cxx_std_17)
  target_include_directories(getdoc-eurooffice-cef-empty-view-lab PRIVATE
      "${_sdk}/include"
      "${_qt_include}"
  )
  target_link_libraries(getdoc-eurooffice-cef-empty-view-lab PRIVATE
      Qt6::Core Qt6::Gui Qt6::Widgets Qt6::OpenGLWidgets
      "${GETDOC_ASC_DOC_SCORE}" "${GETDOC_ASC_QT_SCORE}"
  )
  message(WARNING "GD-31J.4 EMPTY VIEW ONLY: do NOT open files, print, network or integrate into GET.")
endif()

# GD-31J.6 explicitly opt-in test-only executable. It can use a CEF runtime
# without a sandbox; NEVER ship it or attach work/document storage to it.
if(GETDOC_GD31J6_ENABLE_STAGED_OPEN_LAB)
  add_executable(getdoc-eurooffice-cef-staged-open-lab cef_staged_open.cpp)
  target_compile_features(getdoc-eurooffice-cef-staged-open-lab PRIVATE cxx_std_17)
  target_include_directories(getdoc-eurooffice-cef-staged-open-lab PRIVATE
      "${_sdk}/include"
      "${_qt_include}"
  )
  target_link_libraries(getdoc-eurooffice-cef-staged-open-lab PRIVATE
      Qt6::Core Qt6::Gui Qt6::Widgets Qt6::OpenGLWidgets
      "${GETDOC_ASC_DOC_SCORE}" "${GETDOC_ASC_QT_SCORE}"
  )
  if(GETDOC_GD31J22_ENABLE_BINDING_LAB)
    target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
        GETDOC_GD31J22_BINDING_LAB=1)
    message(WARNING "GD-31J.22 synthetic GetLocalFilePath evidence only; not confirmed OOXML import or editing.")
  endif()
  if(GETDOC_GD31J23_ENABLE_CONVERSION_EVIDENCE_LAB)
    target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
        GETDOC_GD31J23_CONVERSION_EVIDENCE_LAB=1)
    message(WARNING "GD-31J.23 isolated Editor.bin candidate only; never a ready/save receipt.")
  endif()
  if(GETDOC_GD31J24_ENABLE_READY_EVENT_LAB)
    target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
        GETDOC_GD31J24_READY_EVENT_LAB=1)
    message(WARNING "GD-31J.24: untrusted renderer ready signal from manually patched pinned SDK, synthetic VM ONLY; no office edit/save rights.")
  endif()
  if(GETDOC_GD31J25_ENABLE_SYNTHETIC_SAVE_LAB)
    target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
        GETDOC_GD31J25_SYNTHETIC_SAVE_LAB=1)
    message(WARNING "GD-31J.25: saving ONLY disposable native synthetic work copy; quarantine output is not verified save or publish authority.")
  endif()
  if(GETDOC_GD31J26_ENABLE_GUARDED_SAVEAS_LAB)
    target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
        GETDOC_GD31J26_GUARDED_SAVEAS_LAB=1)
    message(WARNING "GD-31J.26: only approved synthetic same-format Save As to private disposable destination, never user files.")
  endif()
  if(GETDOC_GD31J12_ENABLE_WAYLAND_OSR_LAB)
    target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
        GETDOC_GD31J12_WAYLAND_OSR_CAPTURE=1)
    message(WARNING "GD-31J.12: Wayland CEF OnPaint capture only on synthetic fixtures. NO sandbox guarantee; NOT for real documents.")
    if(GETDOC_GD31J13_ENABLE_OSR_STREAM_LAB)
      target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
          GETDOC_GD31J13_OSR_STREAM_LAB=1)
      message(WARNING "GD-31J.13: 8-frame CEF preview stream, synthetic files only. Requires offline VM/sandbox approval.")
      if(GETDOC_GD31J14_ENABLE_INPUT_LAB)
        target_sources(getdoc-eurooffice-cef-staged-open-lab PRIVATE osr_input.cpp)
        target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
            GETDOC_GD31J14_POINTER_INPUT=1)
        message(WARNING "GD-31J.14: only pointer/wheel/focus; no clicks/keys/IME/clipboard; isolated synthetic Wayland lab.")
        if(GETDOC_GD31J15_ENABLE_CLICK_LAB)
          target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
              GETDOC_GD31J15_CLICK_LAB=1)
          message(WARNING "GD-31J.15: left click/drag on synthetic-only Wayland VM; keyboard/IME/clipboard still forbidden.")
          if(GETDOC_GD31J16_ENABLE_KEYBOARD_LAB)
            target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
                GETDOC_GD31J16_KEYBOARD_LAB=1)
            message(WARNING "GD-31J.16: nav keys/single BMP character only. No system shortcuts/IME/clipboard. Synthetic VM only.")
            if(GETDOC_GD31J17_ENABLE_IME_COMMIT_LAB)
              target_compile_definitions(getdoc-eurooffice-cef-staged-open-lab PRIVATE
                  GETDOC_GD31J17_IME_COMMIT_LAB=1)
              message(WARNING "GD-31J.17: final BMP IME text only, no native preedit, OS clipboard or corporate files.")
            endif()
          endif()
        endif()
      endif()
    endif()
  endif()
  message(WARNING "GD-31J.6 synthetic test documents only, sandbox isolation required.")
endif()
