// Test fixture: the project's own header, which the shared PCH must not include.
#pragma once
#include "FakeEngine/Actor.h"

class APchThing : public AFakeActor
{
};
