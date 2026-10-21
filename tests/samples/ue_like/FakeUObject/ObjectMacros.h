// Test fixture written for VisualXtsi. It imitates the *shape* of Unreal-style
// reflection macros (token pasting with __LINE__ into generated names); it is
// not Unreal Engine code.
#pragma once
#include "ColorList.h"

#define UCLASS(...) BODY_MACRO_COMBINE(CURRENT_FILE_ID, _, __LINE__, _PROLOG)
#define UPROPERTY(...)
#define UFUNCTION(...)

#define BODY_MACRO_COMBINE_INNER(A, B, C, D) A##B##C##D
#define BODY_MACRO_COMBINE(A, B, C, D) BODY_MACRO_COMBINE_INNER(A, B, C, D)

#define GENERATED_BODY(...) BODY_MACRO_COMBINE(CURRENT_FILE_ID, _, __LINE__, _GENERATED_BODY);

#define SAMPLE_DECLARE_CLASS(TClass, TSuperClass) \
private: \
    TClass& operator=(TClass&&); \
public: \
    typedef TSuperClass Super; \
    typedef TClass ThisClass; \
    static const char* StaticClassName() { return #TClass; } \
    static void DefaultConstructor(const int& X) { (void)X; }
