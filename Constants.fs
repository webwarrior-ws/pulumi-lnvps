module Pulumi.LnVps.Constants

/// IDs of images
let Images =
    [
        "Ubuntu2404", 1
    ]

/// IDs of templates.
/// See https://api.lnvps.net/api/v1/vm/templates
let Templates =
    [
        "Tiny", 13, "1 CPU, 1GB RAM, 40GB SSD, Dublin, 2.7 EUR/month"
        "Small", 14, "2 CPU, 2GB RAM, 80GB SSD, Dublin, 5.1 EUR/month"
        "Medium", 15, "4 CPU, 4GB RAM, 160GB SSD, Dublin, 9.9 EUR/month"
    ]

/// Region IDs.
/// See https://api.lnvps.net/api/v1/vm/templates (in "custom_template" array)
let Regions = 
    [ 
        "Dublin", 1
        "Quebec", 2
        "London", 3
    ]
