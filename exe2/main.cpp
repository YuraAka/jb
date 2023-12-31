#include <iostream>
#include <lib1/lib.hpp>
#include "subdir/sub.hpp"
#include <contrib/boost/_/boost/shared_ptr.hpp>
#include <contrib/zlib/_/zlib.h>

int main() {
    std::cout << "Hello, exe with lib: " << get_number() << std::endl;
    std::cout << "Sub works: " << sub() << std::endl;
    boost::shared_ptr<int> iptr;
    z_stream defstream;
    return 0;
}
