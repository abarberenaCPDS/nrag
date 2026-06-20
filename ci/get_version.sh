#!/bin/bash

# get_version.sh - Shell script version of get_version.py

get_version() {
    local release_type="${RELEASE_TYPE:-dev}"
    local version="${VERSION:-}"
    local rev="${REV:-0}"

    # If no version is provided, use current date
    if [ -z "$version" ]; then
        version=$(date +"%Y.%m.%d")
    fi

    # Construct the final version string
    if [ "$release_type" = "dev" ]; then
        final_version="${version}.dev${rev}"
    elif [ "$release_type" = "release" ]; then
        if [ "$rev" -gt 0 ]; then
            final_version="${version}.post${rev}"
        else
            final_version="$version"
        fi
    else
        echo "Error: Invalid release type: $release_type" >&2
        exit 1
    fi

    echo "$final_version"
}

# Call the function and output the result
get_version