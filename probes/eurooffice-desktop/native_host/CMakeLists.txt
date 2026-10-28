cmake_minimum_required(VERSION 3.20)
project(GetDocumentEuroOfficeQtHostProbe VERSION 0.1 LANGUAGES CXX)

# A small real Qt 6 process for transport/window-lifetime validation only.
# No FetchContent, external download, or production SDK linkage.
find_package(Qt6 6.2 REQUIRED COMPONENTS Core Gui Widgets)
add_executable(getdoc-eurooffice-qt-probe main.cpp)
target_compile_features(getdoc-eurooffice-qt-probe PRIVATE cxx_std_17)
target_link_libraries(getdoc-eurooffice-qt-probe PRIVATE Qt6::Core Qt6::Gui Qt6::Widgets)

enable_testing()
find_package(Python3 COMPONENTS Interpreter QUIET)
if(Python3_Interpreter_FOUND)
  add_test(
    NAME gd31j2_qt_handshake
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_LIST_DIR}/../test_qt_host.py"
            "$<TARGET_FILE:getdoc-eurooffice-qt-probe>")
  set_tests_properties(gd31j2_qt_handshake PROPERTIES TIMEOUT 15)
  # Qt offscreen plugin is required; no real monitor, CEF or GET data touched.
  add_test(
    NAME gd31j5_qt_offscreen_display
    COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_LIST_DIR}/../test_qt_display.py"
            "$<TARGET_FILE:getdoc-eurooffice-qt-probe>")
  set_tests_properties(gd31j5_qt_offscreen_display PROPERTIES TIMEOUT 45)
endif()
