// Test fixture: a commonly included header that does not compile on its own.
// The shared PCH builder must leave it out and still succeed.
#pragma once

UnknownType Broken;
