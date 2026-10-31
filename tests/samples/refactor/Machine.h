// Test fixture: enums, switches and interfaces for the second set of refactorings.
#pragma once

#include "FakeCore.h"

UENUM(BlueprintType)
enum EMachineState
{
    MS_Idle UMETA(DisplayName = "Idle"),
    MS_Running,
    MS_Broken UMETA(Hidden),
    MS_MAX,
};

enum class EPower : uint8
{
    Low,
    Mid,
    High,
    Count
};

namespace Plant
{
enum ELever
{
    Up,
    Down,
    Stuck = Down,
};
}

class IRunnable
{
public:
    virtual ~IRunnable() {}
    virtual void Run(int32 Speed) = 0;
    virtual bool IsRunning() const = 0;
    virtual void Stop() {}
};

class FMachine : public IRunnable
{
public:
    EMachineState State = MS_Idle;
    EPower Power = EPower::Low;
    int32 Level() const;
    void Describe() const;
};