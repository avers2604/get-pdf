#pragma once
// GD-31J.27f: isolated laboratory SaveAs callback counter budget.
// A callback storm must never wrap signed int or produce an unbounded event ID.
// Saturation marks a sticky failure; telemetry is NOT event provenance.
#include <atomic>

namespace Gd31J27F {
inline constexpr int MAX_DIALOG_EVENTS = 64;
inline constexpr int MAX_SAVE_EVENTS = 64;
inline constexpr int MAX_SERIAL_EVENTS = 128;

inline int TickBounded(std::atomic<int>& counter, int limit,
                       std::atomic<bool>& overflow) noexcept {
    int value = counter.load(std::memory_order_acquire);
    for (;;) {
        if (value >= limit || value < 0) {
            overflow.store(true, std::memory_order_release);
            return limit;
        }
        if (counter.compare_exchange_weak(
                value, value + 1, std::memory_order_acq_rel,
                std::memory_order_acquire))
            return value + 1;
    }
}
} // namespace Gd31J27F
