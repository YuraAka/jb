#include <iostream>
#include <lib1/lib.hpp>
#include "subdir/sub.hpp"

int main() {
    std::cout << "Hello, exe with lib: " << get_number() << std::endl;
    std::cout << "Sub works: " << sub() << std::endl;
    return 0;
}
