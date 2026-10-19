import { wire } from './core.js';
// The only inbound channel is the native-messaging port opened by wire(). No other message
// listeners are registered and the manifest has no externally_connectable (checked by tests).
wire(chrome);
