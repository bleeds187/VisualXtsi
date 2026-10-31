// Test fixture: switches for Add Missing Case Statements and enum uses for Convert to Scoped Enum.
#include "Machine.h"

int32 FMachine::Level() const
{
    switch (State)
    {
    case MS_Idle:
        return 0;
    default:
        return 1;
    }
}

void FMachine::Describe() const
{
    switch (Power)
    {
    case EPower::Mid:
        break;
    }
    int Index = State;
    Plant::ELever Lever = Plant::Up;
    switch (Lever)
    {
    }
}

int32 Rank(EMachineState S)
{
    if (S == MS_Running)
        return S;
    return S < MS_MAX ? 1 : 0;
}