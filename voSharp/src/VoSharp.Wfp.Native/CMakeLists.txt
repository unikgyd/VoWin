cmake_minimum_required(VERSION 3.24)
project(VoWinHostImsWfp LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "VoWinHostImsWfp requires Windows WFP")
endif()

add_library(VoWinHostImsWfp SHARED HostImsWfp.cpp)
target_compile_definitions(VoWinHostImsWfp PRIVATE VOWIN_IMS_WFP_BUILD)
target_compile_features(VoWinHostImsWfp PRIVATE cxx_std_17)
target_link_libraries(VoWinHostImsWfp PRIVATE Fwpuclnt Iphlpapi)

enable_testing()
add_executable(VoWinHostImsWfpTests HostImsWfpTests.cpp)
target_compile_features(VoWinHostImsWfpTests PRIVATE cxx_std_17)
target_link_libraries(VoWinHostImsWfpTests PRIVATE VoWinHostImsWfp)
add_test(NAME VoWinHostImsWfpGuards COMMAND VoWinHostImsWfpTests)

add_executable(VoWinHostImsWfpLab EXCLUDE_FROM_ALL HostImsWfpLab.cpp)
target_compile_features(VoWinHostImsWfpLab PRIVATE cxx_std_17)
target_link_libraries(VoWinHostImsWfpLab PRIVATE VoWinHostImsWfp Fwpuclnt Iphlpapi Ws2_32)

# Isolated, opt-in non-loopback Windows-native ESP labs. The Wintun DLL is
# supplied explicitly at runtime and is not distributed here.
add_executable(VoWinWintunImsLab EXCLUDE_FROM_ALL WintunImsLab.cpp)
target_compile_features(VoWinWintunImsLab PRIVATE cxx_std_17)
target_link_libraries(VoWinWintunImsLab PRIVATE VoWinHostImsWfp Iphlpapi Ws2_32 Bcrypt)

# Read-only binding probe for an already connected, Windows-owned IMS PPP link.
add_executable(VoWinNpcapInterfaceProbe EXCLUDE_FROM_ALL NpcapInterfaceProbe.cpp)
target_compile_features(VoWinNpcapInterfaceProbe PRIVATE cxx_std_17)
target_link_libraries(VoWinNpcapInterfaceProbe PRIVATE Iphlpapi)
