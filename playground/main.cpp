#include <iostream>
#include <boost/date_time/posix_time/posix_time.hpp>
#include <boost/date_time/time_facet.hpp>
#include <sstream>
#include <chrono>
#include <iomanip>
#include <ctime>

int main() {
    boost::posix_time::ptime now = boost::posix_time::microsec_clock::local_time();
    std::string formattedTime = boost::posix_time::to_iso_extended_string(now);
    std::cout << formattedTime << std::endl;

    return 0;
}
