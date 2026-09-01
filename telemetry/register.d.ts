/**
 * Side-effect activation entry — importing it runs init():
 *
 *   NODE_OPTIONS="--import @insidebeehive/telemetry/register"   // zero-code
 *   import "@insidebeehive/telemetry/register";                 // in-code, first line of the entrypoint
 */
export {};
