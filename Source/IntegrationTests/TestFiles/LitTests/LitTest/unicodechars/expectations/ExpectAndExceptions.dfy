// RUN: ! %run --target cs "%s" > "%t"
// RUN: ! %run --target go "%s" >> "%t"
// RUN: ! %run --target java "%s" >> "%t"
// RUN: ! %run --target js "%s" >> "%t"
// RUN: ! %run --target py "%s" >> "%t"
// RUN: %diff "%s.expect" "%t"
include "../../expectations/ExpectAndExceptions.dfy"
