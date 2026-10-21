// Test fixture imitating a reflection-tool generated header (see ObjectMacros.h).
#pragma once

#ifdef CURRENT_FILE_ID
#undef CURRENT_FILE_ID
#endif
#define CURRENT_FILE_ID Sample_MyActor_h

#define SAMPLE_REFLECTION_ACCESSORS(T) \
public: \
    static int StaticPropertyCount() { return 1; } \
    const char* GetTypeName() const { return #T "_Generated"; }

#define Sample_MyActor_h_9_PROLOG
#define Sample_MyActor_h_12_GENERATED_BODY \
    SAMPLE_DECLARE_CLASS(AMyActor, ABaseActor) \
    SAMPLE_REFLECTION_ACCESSORS(AMyActor)
