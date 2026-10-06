// Keep the frontend's production configuration and bound build concurrency.
// Webpack's default minifier pool otherwise scales with the server's CPU count.
const production = require('./webpack.prod.js');
const MinimizerPlugin = require('minimizer-webpack-plugin');

module.exports = {
    ...production,
    parallelism: 2,
    optimization: {
        ...production.optimization,
        minimizer: [new MinimizerPlugin({
            parallel: 1,
            // Match webpack's default JavaScript minimizer settings.
            minimizerOptions: { compress: { passes: 2 } }
        })]
    }
};
