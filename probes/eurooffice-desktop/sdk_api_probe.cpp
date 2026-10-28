// GD-31J: compile-only API compatibility probe against a PINNED upstream
// EuroOffice DesktopEditors tree. NOT a runnable editor and NOT a C ABI.
// The absence of linker/runtime checks is intentional; use the full upstream
// build before implementing the separate-process Qt/CEF host.
#include <type_traits>
#include <string>
#include <utility>
#include "applicationmanager.h"
#include "cefview.h"
#include "qcefview.h"
#include "qascapplicationmanager.h"

static_assert(std::is_base_of_v<QWidget, QCefView>,
              "EuroOffice's view must still be an owned Qt QWidget.");
static_assert(std::is_base_of_v<CCefViewWidgetImpl, QCefView>,
              "QCefView must implement the Chromium view host interface.");
static_assert(std::is_base_of_v<CAscApplicationManager, QAscApplicationManager>,
              "Qt application manager API has changed.");

static_assert(std::is_same_v<
                  decltype(std::declval<CAscApplicationManager&>().CreateCefEditor(
                      std::declval<CCefViewWidgetImpl*>())),
                  CCefViewEditor*>,
              "CreateCefEditor signature changed.");
static_assert(std::is_same_v<
                  decltype(std::declval<CCefViewEditor&>().OpenLocalFile(
                      std::declval<const std::wstring&>(),
                      std::declval<const int&>(),
                      std::declval<const std::wstring&>())),
                  void>,
              "OpenLocalFile signature changed.");
static_assert(std::is_same_v<
                  decltype(std::declval<CCefViewEditor&>().GetLocalFilePath()),
                  std::wstring>,
              "GetLocalFilePath signature changed.");

static_assert(ASC_MENU_EVENT_TYPE_CEF_SAVE == 3001,
              "Save event contract changed; native adapter must be reviewed.");
static_assert(ASC_MENU_EVENT_TYPE_CEF_ONSAVE == 3002,
              "OnSave event contract changed.");
static_assert(ASC_MENU_EVENT_TYPE_CEF_PRINT_START == 5011,
              "Print event contract changed.");

namespace gd31j {
// Referenced only to guarantee this translation unit really instantiates the
// checks. It creates no windows, loads no documents and never writes files.
constexpr int kUpstreamHeaderProbeVersion = 1;
}
