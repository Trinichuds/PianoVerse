cmake_minimum_required(VERSION 3.21)
project(NativePianoBackend LANGUAGES CXX)

add_library(NativePianoBackend SHARED
    src/NativePianoBackend.cpp
)

target_compile_features(NativePianoBackend PRIVATE cxx_std_17)
target_compile_definitions(NativePianoBackend PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(NativePianoBackend PRIVATE ole32 avrt uuid)

set_target_properties(NativePianoBackend PROPERTIES
    OUTPUT_NAME "NativePianoBackend"
)
