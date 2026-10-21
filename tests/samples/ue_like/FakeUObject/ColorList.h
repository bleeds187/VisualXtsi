// Test fixture: an X-macro list that #undefs X afterwards. Expansions elsewhere
// that merely mention an identifier X must not pick up this stale definition.
#pragma once

enum class ESampleColor
{
#define X(Name) Name,
    X(Red)
    X(Green)
#undef X
};
