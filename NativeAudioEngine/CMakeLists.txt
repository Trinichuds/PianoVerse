cmake_minimum_required(VERSION 3.21)
project(NativePianoBackend LANGUAGES CXX)

include(FetchContent)

FetchContent_Declare(
    juce
    GIT_REPOSITORY https://github.com/juce-framework/JUCE.git
    GIT_TAG 8.0.12
    GIT_SHALLOW TRUE
)

FetchContent_MakeAvailable(juce)

set(JUCE_MODULE_DIR "${juce_SOURCE_DIR}/modules")

add_library(NativePianoBackend SHARED
    src/JucePianoBackend.cpp
    "${JUCE_MODULE_DIR}/juce_core/juce_core.cpp"
    "${JUCE_MODULE_DIR}/juce_core/juce_core_CompilationTime.cpp"
    "${JUCE_MODULE_DIR}/juce_events/juce_events.cpp"
    "${JUCE_MODULE_DIR}/juce_audio_basics/juce_audio_basics.cpp"
    "${JUCE_MODULE_DIR}/juce_audio_devices/juce_audio_devices.cpp"
)

target_compile_features(NativePianoBackend PRIVATE cxx_std_17)
target_compile_definitions(NativePianoBackend PRIVATE
    WIN32_LEAN_AND_MEAN
    NOMINMAX
    JUCE_GLOBAL_MODULE_SETTINGS_INCLUDED=1
    JUCE_STANDALONE_APPLICATION=1
    JUCE_ASIO=1
    JUCE_ASIO_USE_EXTERNAL_SDK=0
    JUCE_WASAPI=1
    JUCE_DIRECTSOUND=0
    JUCE_WIN32=1
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    _CRT_SECURE_NO_WARNINGS
)
target_include_directories(NativePianoBackend PRIVATE
    "${JUCE_MODULE_DIR}"
)
target_link_libraries(NativePianoBackend PRIVATE
    avrt
    ksuser
    ole32
    shlwapi
    user32
    version
    winmm
    ws2_32
)
target_compile_options(NativePianoBackend PRIVATE
    /bigobj
    /EHsc
)

set_target_properties(NativePianoBackend PROPERTIES
    OUTPUT_NAME "NativePianoBackend"
)
