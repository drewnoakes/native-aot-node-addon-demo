const addon = require('./NativeAddon.node');

// Call our .NET Native AOT functions from JavaScript
console.log(addon.greet('World'));
console.log(`2 + 3 = ${addon.add(2, 3)}`);
